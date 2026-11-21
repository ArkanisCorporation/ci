#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:property NoWarn=SYSLIB1045

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/*
 * Summary:
 *   Keeps a Helm release recoverable around a deployment this platform does not control.
 *
 * Remarks:
 *   `aspire deploy` runs `helm upgrade --install ... --wait` with a closed argument list: no rollback on failure,
 *   so a failed deploy leaves the release failed and half-rolled (ARK-744). This script is the part Helm's
 *   `--rollback-on-failure` would otherwise do, plus the diagnostics and the schema hook Helm has no notion of.
 *
 *   Two phases, selected by DEPLOY_GUARD_PHASE and driven by environment variables only:
 *     capture   Before the deploy. Records the release's revisions, recovers a release a cancelled run left
 *               in a pending state, and remembers the schema-rollback CronJob's identity (uid and generation).
 *               In exclusive mode (DEPLOY_GUARD_EXCLUSIVE=true) it then stops the release: every Deployment it
 *               owns is scaled to 0 and the guard waits until none of their pods exists, so a migration the
 *               previous version cannot tolerate never runs under it. Downtime is the price, by design.
 *     finalize  After the deploy, always. Verifies rollouts on success. On failure it collects diagnostics,
 *               rolls the release back, runs the schema-rollback hook, verifies again, and reports.
 *
 *   It deliberately references no NuGet package: the capture phase compiles it before anything is deployed, so a
 *   runner that cannot build it fails closed, and the finalize phase then reuses that build instead of depending
 *   on a package restore at the moment a rollback is needed.
 *
 *   Never reads Secret objects, never stores pod environment values (see RedactDescribe), and keeps the rendered
 *   schema-rollback Job manifest (a pod spec) under RUNNER_TEMP instead of the uploaded report.
 */

return Guard.Execute();

static class Guard
{
    const string PhaseCapture = "capture";
    const string PhaseFinalize = "finalize";

    // Per-command ceiling for read-only cluster queries. Generous for a slow API server, small next to a deploy.
    // This process deadline is the only bound on a kubectl call, on purpose. Never pass kubectl's
    // --request-timeout: that flag makes kubectl skip the in-cluster ServiceAccount credentials and
    // dial localhost:8080, so on a runner pod without a kubeconfig every such call fails while Helm
    // and plain kubectl keep working. That left the first real deployments unverified (ARK-744).
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(90);

    // Helm's --timeout bounds each Kubernetes operation, not the whole command, so the process gets its own cap.
    static readonly TimeSpan HelmProcessGrace = TimeSpan.FromMinutes(2);

    // Helm already waited for the rollout when it reported success, so the confirmation pass needs minutes at most.
    static readonly TimeSpan MaxVerificationWindow = TimeSpan.FromMinutes(5);

    const int MaxLoggedPods = 40;
    const int LogTailLines = 200;
    const int SchemaJobTtlSeconds = 86400;

    /** How many closing lines of the schema rollback Job's log the summary and the report repeat. */
    const int SchemaJobSummaryLines = 6;

    /*
     * Environment variable every container of the schema rollback Job receives: the RFC 3339 UTC second at which
     * this deployment attempt began (capture phase, before any image build or Helm call), e.g. 2026-11-19T10:15:30Z.
     * Part of the consumer contract (docs/deployments.md): the hook must leave alone every schema change it recorded
     * before this instant. Without it a migrator cannot tell its checkpoint from this attempt from the checkpoint of
     * an earlier, successful deploy of the same version, and would revert the schema under the restored release.
     */
    public const string DeployStartedAtVariable = "ARKANIS_DEPLOY_STARTED_AT";

    const string DeployStartedAtFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /*
     * Environment variable the workflow exports into the deploy step, set to "true", only for an exclusive deployment
     * whose capture phase stopped the previous version (or found none to stop). Part of the consumer contract
     * (docs/deployments.md): the AppHost reads it at publish time, and the migrator may apply a migration marked
     * breaking only when it is "true". It is never set otherwise, so a rolling deploy keeps refusing such migrations.
     */
    public const string DeployExclusiveVariable = "ARKANIS_DEPLOY_EXCLUSIVE";

    public static int Execute()
    {
        try
        {
            var phase = Environment.GetEnvironmentVariable("DEPLOY_GUARD_PHASE") ?? string.Empty;
            return phase switch
            {
                PhaseCapture => CaptureState(Settings.FromEnvironment()),
                PhaseFinalize => FinalizeDeploy(Settings.FromEnvironment()),
                _ => throw new GuardInputException($"DEPLOY_GUARD_PHASE must be '{PhaseCapture}' or '{PhaseFinalize}'."),
            };
        }
        catch (GuardInputException ex)
        {
            Annotate("error", ex.Message);
            return 2;
        }
    }

    // ---------------------------------------------------------------- capture

    static int CaptureState(Settings settings)
    {
        ResetDirectory(settings.ReportDirectory);
        // A persistent runner may still hold the record of an earlier deployment; it must not be mistaken for this one.
        foreach (var stale in new[] { settings.ShimRecordPath, settings.ShimRecordPath + ".history.json" })
        {
            if (settings.ShimRecordPath.Length > 0 && File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        var state = new GuardState
        {
            Release = settings.ReleaseName,
            Namespace = settings.Namespace,

            // Taken first, before anything this attempt could change, so it is a lower bound for all of it.
            DeployStartedAt = DateTime.UtcNow.ToString(DeployStartedAtFormat, CultureInfo.InvariantCulture),
        };

        // 2 x rollout-timeout covers the deploy's own wait plus the rollback's; the rest is build, push and reporting.
        // An exclusive deployment adds a third: the wait for the stopped release's pods to disappear.
        var needed = (settings.RolloutTimeout * (settings.Exclusive ? 3 : 2)) + TimeSpan.FromMinutes(10);
        if (settings.JobTimeoutMinutes > 0 && TimeSpan.FromMinutes(settings.JobTimeoutMinutes) < needed)
        {
            Annotate("warning", $"timeout-minutes ({settings.JobTimeoutMinutes}) leaves little room for a rollback after rollout-timeout {settings.RolloutTimeoutText}. "
                + $"Use at least {Math.Ceiling(needed.TotalMinutes).ToString(CultureInfo.InvariantCulture)} so a failing deploy is not cancelled mid-rollback.");
        }

        Console.WriteLine($"Helm release: {state.Release} (namespace {state.Namespace})");

        // Reading the release is the precondition for ever restoring it. Unreadable means do not deploy.
        var history = ReadHistory(state.Release, state.Namespace, out var readError);
        if (readError is not null)
        {
            Annotate("error", $"Cannot read Helm release '{state.Release}' in namespace '{state.Namespace}': {readError} Nothing was deployed.");
            state.Recovery = "unreadable";
            SaveState(settings, state);
            return 1;
        }

        var latest = Latest(history);
        if (latest is not null && latest.Status.StartsWith("pending-", StringComparison.Ordinal))
        {
            var good = LastDeployed(history);
            if (good is null)
            {
                Annotate("error", $"Helm release '{state.Release}' is stuck in '{latest.Status}' at revision {Text(latest.Revision)} and has no deployed revision to return to. "
                    + $"A cancelled first install needs a human: inspect it, then run 'helm uninstall {state.Release} --namespace {state.Namespace}' if nothing in it must survive. Nothing was deployed.");
                state.History = history;
                state.Recovery = "impossible";
                SaveState(settings, state);
                return 1;
            }

            Console.WriteLine($"Release is stuck in '{latest.Status}' at revision {Text(latest.Revision)}; rolling back to deployed revision {Text(good.Revision)} before deploying.");
            var rollback = HelmRollback(settings, state.Release, state.Namespace, good.Revision);
            if (rollback.ExitCode != 0)
            {
                Annotate("error", $"Recovering stuck Helm release '{state.Release}' failed (helm rollback to revision {Text(good.Revision)}). Nothing was deployed.");
                state.History = history;
                state.Recovery = "failed";
                SaveState(settings, state);
                return 1;
            }

            state.Recovery = "recovered";
            state.RecoveredFromStatus = latest.Status;
            history = ReadHistory(state.Release, state.Namespace, out readError);
            if (readError is not null)
            {
                Annotate("error", $"Cannot re-read Helm release '{state.Release}' after recovering it: {readError} Nothing was deployed.");
                state.Recovery = "unreadable";
                SaveState(settings, state);
                return 1;
            }
        }

        state.History = history;
        state.SchemaCronJobIdentity = ReadCronJobIdentity(settings, state.Namespace);

        // Last step before the deploy may start: everything above must be recorded first, so a failed stop can
        // still be reported, and the stop itself must be the final thing that happens before the deploy.
        if (settings.Exclusive && !StopReleaseForExclusiveDeploy(settings, state))
        {
            SaveState(settings, state);
            WriteOutput(settings, "helm-release", state.Release);
            WriteOutput(settings, "exclusive", "false");
            return 1;
        }

        state.Captured = true;
        SaveState(settings, state);

        latest = Latest(history);
        var lastGood = LastDeployed(history);
        Console.WriteLine(latest is null
            ? "No existing release: this is a first install, there is nothing to roll back to."
            : $"Current revision {Text(latest.Revision)} ({latest.Status}); last deployed revision {(lastGood is null ? "none" : Text(lastGood.Revision))}.");

        WriteOutput(settings, "helm-release", state.Release);
        WriteOutput(settings, "previous-revision", latest is null ? string.Empty : Text(latest.Revision));
        WriteOutput(settings, "recovered", state.Recovery == "recovered" ? "true" : "false");
        WriteOutput(settings, "deploy-started-at", state.DeployStartedAt);
        WriteOutput(settings, "exclusive", settings.Exclusive ? "true" : "false");
        Console.WriteLine($"Deployment attempt started at {state.DeployStartedAt} ({DeployStartedAtVariable} for the schema rollback Job).");
        if (settings.Exclusive)
        {
            Console.WriteLine($"Exclusive deployment: the deploy step receives {DeployExclusiveVariable}=true.");
        }

        return 0;
    }

    // ---------------------------------------------------------------- exclusive deployment

    /*
     * Stops every Deployment the release owns before an exclusive deployment and waits until none of their pods
     * exists any more, terminating pods included: a terminating pod can still hold connections and write with the
     * old code. The replica counts are written to the state file before the first scale call, so a failure at any
     * point can put them back. On failure it restores them, verifies the rollouts, and returns false: the deploy
     * must not start.
     *
     * StatefulSets are deliberately left running. In an Aspire chart they are the data tier (a database or broker
     * container with a volume), which the new version's migrator needs in order to migrate; stopping the database
     * would only guarantee that the migration fails. The application code whose schema expectations break lives in
     * Deployments. DaemonSets cannot be scaled; Jobs and CronJobs are not long-running servers.
     */
    static bool StopReleaseForExclusiveDeploy(Settings settings, GuardState state)
    {
        var exclusive = state.Exclusive;
        exclusive.Enabled = true;
        if (state.History.Count == 0)
        {
            exclusive.Status = ExclusiveStatuses.FirstInstall;
            Console.WriteLine("Exclusive deployment of a first install: nothing runs yet, so nothing is stopped.");
            return true;
        }

        var workloads = ReadWorkloads(state.Release, state.Namespace, out var listed);
        if (!listed)
        {
            Annotate("error", $"EXCLUSIVE STOP FAILED: could not list the Deployments of release '{state.Release}'. Nothing was stopped and nothing was deployed.");
            exclusive.Status = ExclusiveStatuses.StopFailed;
            exclusive.Restore = ExclusiveRestores.NotNeeded;
            return false;
        }

        var deployments = workloads.Where(workload => workload.Kind == "Deployment").ToList();
        if (deployments.Count == 0)
        {
            Annotate("warning", $"Exclusive deployment, but no Deployment carries release '{state.Release}''s Helm ownership annotation. Nothing was stopped.");
            exclusive.Status = ExclusiveStatuses.NoDeployments;
            return true;
        }

        var unselectable = deployments.Where(deployment => deployment.Selector.Count == 0).Select(deployment => deployment.Name).ToList();
        if (unselectable.Count > 0)
        {
            Annotate("error", $"EXCLUSIVE STOP FAILED: these Deployments have no matchLabels selector, so their pods cannot be confirmed gone: {string.Join(", ", unselectable)}. Nothing was stopped and nothing was deployed.");
            exclusive.Status = ExclusiveStatuses.StopFailed;
            exclusive.Restore = ExclusiveRestores.NotNeeded;
            return false;
        }

        exclusive.Workloads = [.. deployments.Select(deployment => new ExclusiveWorkload(deployment.Name, deployment.Replicas, deployment.Selector))];
        exclusive.DowntimeStartedAt = Now();
        SaveState(settings, state);
        Console.WriteLine($"Exclusive deployment: stopping {string.Join(", ", exclusive.Workloads.Select(workload => $"{workload.Name} ({Text(workload.Replicas)} replicas)"))}. The service is down from {exclusive.DowntimeStartedAt}.");

        if (StopDeployments(settings, exclusive.Workloads, state.Namespace, "before the deployment"))
        {
            exclusive.Status = ExclusiveStatuses.Stopped;
            Console.WriteLine("Every pod of the release is gone; the deployment may migrate.");
            return true;
        }

        Annotate("error", "EXCLUSIVE STOP FAILED: the release could not be stopped. Nothing was deployed; restoring the recorded replica counts.");
        exclusive.Status = ExclusiveStatuses.StopFailed;
        var failed = new List<string>();
        RestoreExclusiveReplicas(settings, exclusive, state.Namespace, onlyWhereZero: false, failed, "after the failed stop");
        return false;
    }

    /*
     * Scales the given Deployments to 0 and waits, bounded by rollout-timeout, until no pod any of them selects is
     * left. A pod list that cannot be read counts as "not gone yet": the wait only ends early on proof.
     */
    static bool StopDeployments(Settings settings, List<ExclusiveWorkload> targets, string ns, string when)
    {
        Console.WriteLine($"::group::Stop Deployments {when}");
        try
        {
            foreach (var target in targets)
            {
                var scale = Run("kubectl", ["scale", $"deployment/{target.Name}", "--namespace", ns, "--replicas=0"], QueryTimeout, echoOutput: true);
                if (scale.ExitCode != 0)
                {
                    Annotate("error", $"Scaling deployment/{target.Name} to 0 failed: {FirstLine(scale.Stderr, "kubectl scale failed.")}");
                    return false;
                }
            }

            var selectors = targets.Select(target => new Workload("Deployment", target.Name, target.Selector, RolledOut: false, Replicas: 0)).ToList();
            var elapsed = Stopwatch.StartNew();
            var lastReported = -1;
            while (true)
            {
                var pods = ReadPods(ns, out var listed);
                var remaining = listed ? pods.Where(pod => selectors.Any(selector => selector.Selects(pod))).Select(pod => pod.Name).ToList() : null;
                if (remaining is { Count: 0 })
                {
                    Console.WriteLine($"No pod of {string.Join(", ", targets.Select(target => target.Name))} is left ({Text((int)elapsed.Elapsed.TotalSeconds)}s).");
                    return true;
                }

                if (elapsed.Elapsed >= settings.RolloutTimeout)
                {
                    Annotate("error", remaining is null
                        ? $"The pods could not be listed within {settings.RolloutTimeoutText}, so the release is not known to be stopped."
                        : $"These pods still exist after {settings.RolloutTimeoutText}: {string.Join(", ", remaining)}.");
                    return false;
                }

                var count = remaining?.Count ?? -1;
                if (count != lastReported)
                {
                    Console.WriteLine(remaining is null ? "Pods could not be listed; retrying." : $"Waiting for {Text(count)} pods to disappear: {string.Join(", ", remaining)}");
                    lastReported = count;
                }

                Thread.Sleep(settings.PollInterval);
            }
        }
        finally
        {
            Console.WriteLine("::endgroup::");
        }
    }

    /*
     * Puts the recorded replica counts back after an exclusive deployment, then verifies the rollouts. Helm restores
     * them when it re-applies a release (upgrade or rollback), but that is not assumed: each Deployment is read and
     * scaled when it is not where it should be. onlyWhereZero is for a successful deploy, whose chart may change the
     * replica count on purpose; only a Deployment still at 0 is then put back. A Deployment the release no longer has
     * is left alone. Sets the restore result and, when every rollout is verified, the end of the downtime.
     */
    static bool RestoreExclusiveReplicas(Settings settings, ExclusiveState exclusive, string ns, bool onlyWhereZero, List<string> failed, string when)
    {
        var ok = true;
        var present = new List<ExclusiveWorkload>();
        Console.WriteLine($"::group::Restore replica counts {when}");
        foreach (var target in exclusive.Workloads)
        {
            var read = Run("kubectl", ["get", "deployment", target.Name, "--namespace", ns, "--ignore-not-found", "--output", "jsonpath={.spec.replicas}"], QueryTimeout);
            if (read.ExitCode != 0)
            {
                Annotate("error", $"Could not read deployment/{target.Name}: {FirstLine(read.Stderr, "kubectl get failed.")}");
                failed.Add($"deployment/{target.Name}");
                ok = false;
                continue;
            }

            if (read.Stdout.Trim().Length == 0)
            {
                Console.WriteLine($"deployment/{target.Name} no longer exists in the release; nothing to restore.");
                continue;
            }

            present.Add(target);
            var current = long.TryParse(read.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            if (current == target.Replicas || (onlyWhereZero && current != 0))
            {
                Console.WriteLine($"deployment/{target.Name} is at {Text(current)} replicas.");
                continue;
            }

            var scale = Run("kubectl", ["scale", $"deployment/{target.Name}", "--namespace", ns, $"--replicas={Text(target.Replicas)}"], QueryTimeout, echoOutput: true);
            if (scale.ExitCode != 0)
            {
                Annotate("error", $"Restoring deployment/{target.Name} to {Text(target.Replicas)} replicas failed: {FirstLine(scale.Stderr, "kubectl scale failed.")}");
                failed.Add($"deployment/{target.Name}");
                ok = false;
                continue;
            }

            exclusive.RestoredByGuard.Add(target.Name);
        }

        Console.WriteLine("::endgroup::");
        var notRolledOut = present.Count == 0 ? [] : VerifyRollouts(settings, [.. present.Select(target => new Workload("Deployment", target.Name, target.Selector, RolledOut: false, Replicas: target.Replicas))], ns, when);
        foreach (var name in notRolledOut.Where(name => !failed.Contains(name)))
        {
            failed.Add(name);
        }

        ok = ok && notRolledOut.Count == 0;
        exclusive.Restore = ok ? ExclusiveRestores.Restored : ExclusiveRestores.Failed;
        if (ok)
        {
            exclusive.DowntimeEndedAt = Now();
        }

        return ok;
    }

    static string Now() => DateTime.UtcNow.ToString(DeployStartedAtFormat, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- finalize

    static int FinalizeDeploy(Settings settings)
    {
        Directory.CreateDirectory(settings.ReportDirectory);
        var report = new Report { RolloutTimeout = settings.RolloutTimeoutText, DeployStepOutcome = settings.DeployOutcome };
        var state = LoadState(settings);

        if (state is null)
        {
            report.Release = settings.ReleaseName;
            report.Namespace = settings.Namespace;
            report.Outcome = Outcomes.NotAttempted;
            report.Detail = "The release state was never captured, so the deployment did not start.";
            return Publish(settings, report);
        }

        report.Release = state.Release;
        report.Namespace = state.Namespace;
        report.Recovery = state.Recovery;
        report.Exclusive = state.Exclusive;
        var baseline = state.History;

        // Exclusive deployment whose stop failed in the capture phase: the deploy never ran, and the capture phase
        // already tried to restore the replica counts. Its own outcome, so a caller can tell it from a deploy failure.
        if (state.Exclusive.Status == ExclusiveStatuses.StopFailed)
        {
            SetRevisions(report, baseline, null);
            if (state.Exclusive.Restore == ExclusiveRestores.Failed)
            {
                report.Outcome = Outcomes.RollbackFailed;
                report.Detail = "Stopping the release for an exclusive deployment failed, and restoring its replica counts failed too. Nothing was deployed.";
            }
            else
            {
                report.Outcome = Outcomes.FailedExclusiveStop;
                report.Detail = state.Exclusive.Restore == ExclusiveRestores.NotNeeded
                    ? "Stopping the release for an exclusive deployment failed before anything was scaled. Nothing was deployed."
                    : "Stopping the release for an exclusive deployment failed. Nothing was deployed; the recorded replica counts are restored and rolled out.";
            }

            return Publish(settings, report);
        }

        if (!state.Captured)
        {
            SetRevisions(report, baseline, null);
            report.Outcome = Outcomes.RollbackFailed;
            report.Detail = state.Recovery switch
            {
                "unreadable" => "The Helm release could not be read before deploying. Nothing was deployed.",
                "impossible" => "The release is stuck in a pending state with no deployed revision to return to. Nothing was deployed.",
                _ => "Recovering the stuck release failed before deploying. Nothing was deployed.",
            };
            return Publish(settings, report);
        }

        var stopped = state.Exclusive.Status == ExclusiveStatuses.Stopped;
        if (settings.DeployOutcome == "skipped")
        {
            SetRevisions(report, baseline, null);
            report.Outcome = Outcomes.NotAttempted;
            report.Detail = "An earlier step failed, so the deployment did not start. The release is untouched.";
            if (stopped && !RestoreExclusiveReplicas(settings, state.Exclusive, state.Namespace, onlyWhereZero: false, report.FailedResources, "after a deployment that did not start"))
            {
                report.Outcome = Outcomes.RollbackFailed;
                report.Detail = "The deployment did not start, but the release stopped for it could not be restored to its recorded replica counts.";
            }

            return Publish(settings, report);
        }

        if (settings.DeployOutcome == "cancelled")
        {
            SetRevisions(report, baseline, null);
            report.Outcome = Outcomes.Cancelled;
            report.Detail = "The deployment was cancelled or timed out. No rollback was attempted; the next run recovers a release left pending before it deploys.";

            // A stopped release Helm never touched is safe to start again: same manifests as before the run. Once Helm
            // created a revision the pods would start a half-applied release, so that is left to the next run's recovery.
            if (stopped)
            {
                var untouched = ReadHistory(state.Release, state.Namespace, out var cancelReadError);
                if (cancelReadError is null && (Latest(untouched)?.Revision ?? 0) == (Latest(baseline)?.Revision ?? 0))
                {
                    report.Detail = "The deployment was cancelled before Helm changed the release. The release stopped for it is restored to its recorded replica counts.";
                    if (!RestoreExclusiveReplicas(settings, state.Exclusive, state.Namespace, onlyWhereZero: false, report.FailedResources, "after the cancelled deployment"))
                    {
                        report.Detail = "The deployment was cancelled before Helm changed the release, and restoring the stopped release's replica counts failed.";
                    }
                }
                else
                {
                    report.Detail = "The exclusive deployment was cancelled after Helm started changing the release. Its Deployments may still be scaled to 0; the next run recovers a release left pending.";
                }
            }

            return Publish(settings, report);
        }

        // The AppHost decides the real release name and namespace. When the shim saw `helm upgrade` target a
        // different release than the one captured, trust what actually ran and use the history it recorded.
        var shim = ShimRecord.TryRead(settings.ShimRecordPath);
        var release = state.Release;
        var ns = state.Namespace;
        if (shim is not null && (shim.Release != release || shim.Namespace != ns))
        {
            Annotate("warning", $"The AppHost deployed Helm release '{shim.Release}' in namespace '{shim.Namespace}', not '{release}' in '{ns}'. "
                + $"Set helm-release-name: {shim.Release} (and the matching kubernetes-namespace) so a stuck release can be recovered before deploying.");
            release = shim.Release;
            ns = shim.Namespace;
            baseline = shim.HistoryBefore;
            report.Release = release;
            report.Namespace = ns;
            report.Adopted = true;
        }

        var deployFailed = settings.DeployOutcome != "success";
        var after = ReadHistory(release, ns, out var afterError);
        if (afterError is not null)
        {
            Annotate("error", $"Cannot read Helm release '{release}' after the deployment: {afterError}");
            SetRevisions(report, baseline, null);
            report.Outcome = Outcomes.RollbackFailed;
            report.Detail = "The release could not be read after the deployment, so its state is unknown.";
            return Publish(settings, report);
        }

        SetRevisions(report, baseline, after);
        if (deployFailed && report.NewRevision is not null)
        {
            // Helm stores why an upgrade failed in the failed revision's description. That is the exact error
            // (a field conflict, a timeout, a rejected object), so nobody has to dig it out of the deploy log.
            report.FailureReason = after.FirstOrDefault(entry => entry.Revision == report.NewRevision && entry.Status == "failed")?.Description.Trim() ?? string.Empty;
        }

        var baselineRevision = Latest(baseline)?.Revision ?? 0;
        var changed = (Latest(after)?.Revision ?? 0) > baselineRevision;

        if (!deployFailed)
        {
            // The new chart decides the replica counts; only a Deployment it left at the exclusive stop's 0 is put back.
            var restoreFailures = new List<string>();
            var restored = !stopped || RestoreExclusiveReplicas(settings, state.Exclusive, state.Namespace, onlyWhereZero: true, restoreFailures, "after the deployment");
            var workloads = ReadWorkloads(release, ns, out var listed);
            var notRolledOut = VerifyRollouts(settings, workloads, ns, "after the deployment");
            notRolledOut.AddRange(restoreFailures.Where(name => !notRolledOut.Contains(name)));
            if (notRolledOut.Count == 0 && restored)
            {
                // Helm's own --wait already vouched for the release, so an unverified rollout stays a
                // successful deployment. The report must still not claim a check that never ran.
                report.RolloutVerified = workloads.Any(workload => workload.Kind == "Deployment");
                report.Outcome = Outcomes.Deployed;
                report.Detail = report.RolloutVerified
                    ? "Helm reported success and every Deployment of the release is rolled out."
                    : listed
                        ? "Helm reported success. The rollout was not verified independently: no Deployment carries this release's Helm ownership annotation."
                        : "Helm reported success. The rollout was not verified independently: the workloads in the namespace could not be listed.";
                return Publish(settings, report);
            }

            if (stopped)
            {
                // The downtime is not over until the rollback below brings the release back.
                state.Exclusive.DowntimeEndedAt = string.Empty;
            }

            Annotate("error", $"Helm reported success but these Deployments are not rolled out: {string.Join(", ", notRolledOut)}. Treating the deployment as failed.");
            report.FailedResources.AddRange(notRolledOut);
            report.FailureReason = $"Helm reported success, but these Deployments did not roll out: {string.Join(", ", notRolledOut)}.";
            report.VerificationFailedAfterHelmSuccess = true;
            changed = true;
        }

        if (!changed)
        {
            CollectDiagnostics(settings, report, release, ns, "before-rollback", full: false);
            report.Outcome = Outcomes.FailedReleaseUnchanged;
            report.Detail = "The deployment failed before Helm created a new revision, so the release is exactly as it was.";
            if (stopped)
            {
                report.Detail = "The deployment failed before Helm created a new revision. The release stopped for it is restored to its recorded replica counts.";
                if (!RestoreExclusiveReplicas(settings, state.Exclusive, state.Namespace, onlyWhereZero: false, report.FailedResources, "after the failed deployment"))
                {
                    report.Outcome = Outcomes.RollbackFailed;
                    report.Detail = "The deployment failed before Helm created a new revision, but the release stopped for it could not be restored to its recorded replica counts.";
                }
            }

            return Publish(settings, report);
        }

        // Diagnostics first: the rollback removes the failing pods and their logs.
        CollectDiagnostics(settings, report, release, ns, "before-rollback", full: true);

        var target = LastDeployed(baseline);
        if (target is null)
        {
            report.Outcome = Outcomes.FailedNoPreviousRelease;
            report.Detail = "There is no previously deployed revision to roll back to (first install). The failed release is left in place for inspection.";
            return Publish(settings, report);
        }

        report.RollbackTargetRevision = target.Revision;
        if (!settings.RollbackOnFailure)
        {
            report.Outcome = Outcomes.FailedRollbackDisabled;
            report.Detail = $"rollback-on-failure is false. Restore manually: helm rollback {release} {Text(target.Revision)} --namespace {ns} --wait";
            return Publish(settings, report);
        }

        // Render the hook Job now: the rollback reverts or deletes the CronJob it is created from.
        var schemaJob = PrepareSchemaRollback(settings, state, report, ns);
        try
        {
            // Exclusive: the previous version cannot run on the failed release's schema, so `helm rollback --wait`
            // would wait for pods that never become ready, and the schema Job would never run. The schema is
            // reverted first instead, with the failed release stopped; the service is down anyway.
            if (stopped && schemaJob is not null)
            {
                RunSchemaRollbackBeforeExclusiveRollback(settings, report, schemaJob, release, ns);
            }

            var rollback = HelmRollback(settings, release, ns, target.Revision);
            if (rollback.ExitCode != 0)
            {
                Annotate("error", $"ROLLBACK FAILED: helm rollback {release} {Text(target.Revision)} did not succeed. The environment needs attention.");
                if (schemaJob is not null && !stopped)
                {
                    report.SchemaRollback = SchemaOutcomes.Skipped;
                    report.SchemaRollbackDetail = "Not run: the Helm rollback failed, so new-version pods may still use the schema.";
                }

                CollectDiagnostics(settings, report, release, ns, "after-rollback", full: false);
                SetRevisions(report, baseline, ReadHistory(release, ns, out _), keepNew: true);
                report.Outcome = Outcomes.RollbackFailed;
                report.Detail = $"helm rollback to revision {Text(target.Revision)} failed.";
                return Publish(settings, report);
            }

            report.HelmRollbackSucceeded = true;
            if (schemaJob is not null && !stopped)
            {
                RunSchemaRollback(settings, report, schemaJob, ns);
            }
        }
        finally
        {
            if (schemaJob is not null && File.Exists(schemaJob.ManifestPath))
            {
                File.Delete(schemaJob.ManifestPath);
            }
        }

        // Helm re-applied the previous manifests, which normally restores the replica counts. Checked, not assumed.
        var stillBroken = new List<string>();
        if (stopped)
        {
            RestoreExclusiveReplicas(settings, state.Exclusive, state.Namespace, onlyWhereZero: false, stillBroken, "after the rollback");
        }

        foreach (var name in VerifyRollouts(settings, ReadWorkloads(release, ns), ns, "after the rollback").Where(name => !stillBroken.Contains(name)))
        {
            stillBroken.Add(name);
        }

        CollectDiagnostics(settings, report, release, ns, "after-rollback", full: false);
        SetRevisions(report, baseline, ReadHistory(release, ns, out _), keepNew: true);

        if (stillBroken.Count > 0)
        {
            state.Exclusive.DowntimeEndedAt = string.Empty;
            Annotate("error", $"ROLLBACK FAILED: after rolling back, these Deployments are still not rolled out: {string.Join(", ", stillBroken)}.");
            foreach (var name in stillBroken.Where(name => !report.FailedResources.Contains(name)))
            {
                report.FailedResources.Add(name);
            }

            report.Outcome = Outcomes.RollbackFailed;
            report.Detail = $"Helm rolled back to revision {Text(target.Revision)} but the workloads did not become ready.";
            return Publish(settings, report);
        }

        if (report.SchemaRollback is SchemaOutcomes.Failed or SchemaOutcomes.TimedOut || report.SchemaRollbackBlocked)
        {
            report.Outcome = Outcomes.RollbackFailed;
            report.Detail = $"Workloads are restored to revision {Text(target.Revision)}, but the schema rollback did not complete. The database needs attention.";
            return Publish(settings, report);
        }

        report.Outcome = Outcomes.RolledBack;
        report.Detail = report.SchemaRollback == SchemaOutcomes.Succeeded
            ? "Every Deployment was verified rolled out after the rollback, and the schema rollback Job completed."
            : "Every Deployment was verified rolled out after the rollback.";
        return Publish(settings, report);
    }

    /*
     * Exclusive failure path: stops every Deployment the failed release owns, then runs the schema rollback Job, both
     * before the Helm rollback. Stopping first keeps the guarantee the rolling path gets from `helm rollback --wait`:
     * no new-version pod uses the schema while it is reverted. A stop that fails leaves the Job unrun and blocks a
     * clean outcome, because the previous version is then about to start on a schema it cannot run on.
     */
    static void RunSchemaRollbackBeforeExclusiveRollback(Settings settings, Report report, SchemaJob job, string release, string ns)
    {
        var failedRelease = ReadWorkloads(release, ns).Where(workload => workload.Kind == "Deployment" && workload.Selector.Count > 0)
            .Select(workload => new ExclusiveWorkload(workload.Name, workload.Replicas, workload.Selector)).ToList();
        if (failedRelease.Count > 0 && !StopDeployments(settings, failedRelease, ns, "before the schema rollback"))
        {
            Annotate("error", "SCHEMA ROLLBACK NOT RUN: the failed release's pods did not stop, so they may still use the schema.");
            report.SchemaRollback = SchemaOutcomes.Skipped;
            report.SchemaRollbackDetail = "Not run: the failed release's pods did not stop before the exclusive rollback.";
            report.SchemaRollbackBlocked = true;
            return;
        }

        RunSchemaRollback(settings, report, job, ns);
    }

    // ---------------------------------------------------------------- helm

    static List<HistoryEntry> ReadHistory(string release, string ns, out string? error)
    {
        error = null;

        // `helm history` fails for a missing release and for an unreachable cluster alike; `helm list` tells them apart.
        var list = Run("helm", ["list", "--namespace", ns, "--filter", $"^{Regex.Escape(release)}$", "--output", "json"], QueryTimeout);
        if (list.ExitCode != 0)
        {
            error = FirstLine(list.Stderr, "helm list failed.");
            return [];
        }

        if (!TryParseArray(list.Stdout, out var releases))
        {
            error = "helm list did not return JSON.";
            return [];
        }

        if (releases.Count == 0)
        {
            return [];
        }

        var history = Run("helm", ["history", release, "--namespace", ns, "--max", "256", "--output", "json"], QueryTimeout);
        if (history.ExitCode != 0)
        {
            error = FirstLine(history.Stderr, "helm history failed.");
            return [];
        }

        var entries = HistoryEntry.Parse(history.Stdout);
        if (entries is null)
        {
            error = "helm history did not return JSON.";
            return [];
        }

        return entries;
    }

    static ProcessResult HelmRollback(Settings settings, string release, string ns, int revision)
    {
        Console.WriteLine($"::group::helm rollback {release} {Text(revision)}");
        string[] arguments = ["rollback", release, Text(revision), "--namespace", ns, "--wait", "--timeout", settings.RolloutTimeoutText];
        if (AppliedServerSide(release, ns))
        {
            // A field someone changed by hand (kubectl edit, kubectl patch) belongs to that tool's field manager,
            // and server-side apply refuses to overwrite it. A rollback must never be blocked by a hotfix.
            arguments = [.. arguments, "--force-conflicts"];
        }

        var result = Run(
            "helm",
            arguments,
            settings.RolloutTimeout + HelmProcessGrace,
            echoOutput: true);
        Console.WriteLine("::endgroup::");
        return result;
    }

    /*
     * True when Helm applies this release server-side. Only then is --force-conflicts valid: on a client-side
     * release Helm rejects the flag outright ("forceConflicts enabled when serverSideApply disabled") and records
     * a failed revision. Anything unreadable counts as "not server-side", which leaves Helm's default behavior.
     * `helm get metadata` carries names, revision and apply method only: no values, no manifest.
     */
    static bool AppliedServerSide(string release, string ns)
    {
        var result = Run("helm", ["get", "metadata", release, "--namespace", ns, "--output", "json"], QueryTimeout, quiet: true);
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(result.Stdout)?["applyMethod"]?.GetValue<string>() == "ssa";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    static HistoryEntry? Latest(List<HistoryEntry> history) => history.MaxBy(entry => entry.Revision);

    static HistoryEntry? LastDeployed(List<HistoryEntry> history) =>
        history.Where(entry => entry.Status == "deployed").MaxBy(entry => entry.Revision);

    static void SetRevisions(Report report, List<HistoryEntry> baseline, List<HistoryEntry>? after, bool keepNew = false)
    {
        var previous = Latest(baseline);
        report.PreviousRevision = previous?.Revision;
        report.PreviousStatus = previous?.Status ?? string.Empty;
        if (after is null)
        {
            return;
        }

        var current = Latest(after);
        report.CurrentRevision = current?.Revision;
        report.CurrentStatus = current?.Status ?? string.Empty;
        if (!keepNew)
        {
            // The revision this deploy created, if Helm got far enough to create one.
            report.NewRevision = current is not null && current.Revision > (previous?.Revision ?? 0) ? current.Revision : null;
        }
    }

    // ---------------------------------------------------------------- workloads

    static List<Workload> ReadWorkloads(string release, string ns) => ReadWorkloads(release, ns, out _);

    // `listed` tells "the release has no workloads" from "the workloads could not be read"; the exclusive stop must not confuse them.
    static List<Workload> ReadWorkloads(string release, string ns, out bool listed)
    {
        var result = Run("kubectl", ["get", "deployments,statefulsets,daemonsets,jobs", "--namespace", ns, "--output", "json"], QueryTimeout);
        var workloads = new List<Workload>();
        listed = result.ExitCode == 0 && TryParseItems(result.Stdout, out _);
        if (!listed || !TryParseItems(result.Stdout, out var items))
        {
            // Say why: this warning once fired on a healthy cluster and named no cause, which left the
            // rollout unverified with nothing to act on. Standard output is only sized, never printed,
            // because workload manifests can carry inline environment values.
            var stderr = string.Join(' ', result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var cause = result.ExitCode != 0
                ? $"kubectl exited with code {Text(result.ExitCode)}"
                : "kubectl exited with code 0 but its output was not a JSON list";
            Annotate("warning", $"Could not list the workloads in the namespace ({cause}; {Text(result.Stdout.Length)} characters on standard output; standard error: {(stderr.Length == 0 ? "empty" : stderr.Length > 600 ? stderr[..600] + "..." : stderr)}). Rollout verification has nothing to check.");
            return workloads;
        }

        foreach (var item in items)
        {
            // Helm stamps every object it manages with its release name, so no Secret-bearing manifest has to be read.
            var metadata = item?["metadata"];
            if (metadata?["annotations"]?["meta.helm.sh/release-name"]?.GetValue<string>() != release)
            {
                continue;
            }

            var selector = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item?["spec"]?["selector"]?["matchLabels"] is JsonObject matchLabels)
            {
                foreach (var label in matchLabels)
                {
                    selector[label.Key] = label.Value?.GetValue<string>() ?? string.Empty;
                }
            }

            workloads.Add(new Workload(
                Kind: item?["kind"]?.GetValue<string>() ?? string.Empty,
                Name: metadata?["name"]?.GetValue<string>() ?? string.Empty,
                Selector: selector,
                RolledOut: IsRolledOut(item),
                Replicas: Number(item?["spec"]?["replicas"]) ?? 1));
        }

        return workloads;
    }

    static bool IsRolledOut(JsonNode? item)
    {
        if (item?["kind"]?.GetValue<string>() != "Deployment")
        {
            return true;
        }

        var desired = Number(item["spec"]?["replicas"]) ?? 1;
        var status = item["status"];
        return (Number(status?["observedGeneration"]) ?? 0) >= (Number(item["metadata"]?["generation"]) ?? 0)
            && (Number(status?["updatedReplicas"]) ?? 0) >= desired
            && (Number(status?["replicas"]) ?? 0) <= (Number(status?["updatedReplicas"]) ?? 0)
            && (Number(status?["availableReplicas"]) ?? 0) >= desired;
    }

    static List<string> VerifyRollouts(Settings settings, List<Workload> workloads, string ns, string when)
    {
        var failed = new List<string>();
        var deployments = workloads.Where(workload => workload.Kind == "Deployment").ToList();
        if (deployments.Count == 0)
        {
            Annotate("warning", $"No Deployment carries this release's Helm ownership annotation, so no rollout was verified {when}.");
            return failed;
        }

        // One deadline for all Deployments: they roll out in parallel, so waiting for each in turn must not add up.
        var window = settings.RolloutTimeout < MaxVerificationWindow ? settings.RolloutTimeout : MaxVerificationWindow;
        var deadline = Stopwatch.StartNew();
        Console.WriteLine($"::group::Verify rollouts {when}");
        foreach (var deployment in deployments)
        {
            var remaining = Math.Max(1, (int)Math.Ceiling((window - deadline.Elapsed).TotalSeconds));
            var result = Run(
                "kubectl",
                ["rollout", "status", $"deployment/{deployment.Name}", "--namespace", ns, $"--timeout={Text(remaining)}s"],
                TimeSpan.FromSeconds(remaining + 30),
                echoOutput: true);
            if (result.ExitCode != 0)
            {
                failed.Add($"deployment/{deployment.Name}");
            }
        }

        Console.WriteLine("::endgroup::");
        return failed;
    }

    // ---------------------------------------------------------------- diagnostics

    static void CollectDiagnostics(Settings settings, Report report, string release, string ns, string stage, bool full)
    {
        var directory = Path.Combine(settings.ReportDirectory, "diagnostics", stage);
        Directory.CreateDirectory(directory);
        Console.WriteLine($"::group::Collect diagnostics ({stage})");
        try
        {
            // Table output only: `helm status --output json` embeds the release's values.
            Save(directory, "helm-status.txt", Run("helm", ["status", release, "--namespace", ns], QueryTimeout));
            Save(directory, "helm-history.txt", Run("helm", ["history", release, "--namespace", ns, "--max", "20"], QueryTimeout));
            Save(directory, "workloads.txt", Run("kubectl", ["get", "deployments,replicasets,pods", "--namespace", ns, "--output", "wide"], QueryTimeout));
            Save(directory, "events.txt", Run("kubectl", ["get", "events", "--namespace", ns, "--sort-by=.lastTimestamp"], QueryTimeout));
            if (!full)
            {
                return;
            }

            var workloads = ReadWorkloads(release, ns);
            foreach (var workload in workloads.Where(workload => !workload.RolledOut))
            {
                AddFailedResource(report, $"deployment/{workload.Name}");
            }

            var pods = ReadPods(ns);
            var owned = workloads.Count == 0
                ? pods
                : pods.Where(pod => workloads.Any(workload => workload.Selects(pod))).ToList();
            if (workloads.Count == 0 && pods.Count > 0)
            {
                Console.WriteLine("No workload carries this release's Helm ownership annotation; collecting from every pod in the namespace.");
            }

            foreach (var pod in owned.Where(pod => !pod.Ready))
            {
                AddFailedResource(report, $"pod/{pod.Name}");
                var describe = Run("kubectl", ["describe", "pod", pod.Name, "--namespace", ns], QueryTimeout);
                File.WriteAllText(Path.Combine(directory, $"describe-{pod.Name}.txt"), RedactDescribe(describe.Stdout) + describe.Stderr);
            }

            if (owned.Count > MaxLoggedPods)
            {
                Console.WriteLine($"Release has {Text(owned.Count)} pods; collecting logs for the {Text(MaxLoggedPods)} least ready.");
            }

            var logDirectory = Path.Combine(directory, "logs");
            Directory.CreateDirectory(logDirectory);
            foreach (var pod in owned.OrderBy(pod => pod.Ready).Take(MaxLoggedPods))
            {
                foreach (var container in pod.Containers)
                {
                    string[] logArgs = ["logs", pod.Name, "--namespace", ns, "--container", container, $"--tail={Text(LogTailLines)}", "--timestamps"];
                    var current = Run("kubectl", logArgs, QueryTimeout);
                    File.WriteAllText(Path.Combine(logDirectory, $"{pod.Name}__{container}.log"), current.Stdout + current.Stderr);

                    // A container that never restarted has no previous log; kubectl says so and that is not an error here.
                    var previous = Run("kubectl", [.. logArgs, "--previous"], QueryTimeout);
                    if (previous.ExitCode == 0 && previous.Stdout.Length > 0)
                    {
                        File.WriteAllText(Path.Combine(logDirectory, $"{pod.Name}__{container}.previous.log"), previous.Stdout);
                    }
                }
            }
        }
        finally
        {
            Console.WriteLine("::endgroup::");
        }
    }

    static void AddFailedResource(Report report, string name)
    {
        if (!report.FailedResources.Contains(name))
        {
            report.FailedResources.Add(name);
        }
    }

    static List<Pod> ReadPods(string ns) => ReadPods(ns, out _, quiet: false);

    static List<Pod> ReadPods(string ns, out bool listed, bool quiet = true)
    {
        var pods = new List<Pod>();

        // Parsed in memory and never written out: a pod object carries its literal environment values.
        // Terminating pods are listed too (they carry a deletionTimestamp), which the exclusive stop relies on.
        var result = Run("kubectl", ["get", "pods", "--namespace", ns, "--output", "json"], QueryTimeout, quiet: quiet);
        listed = result.ExitCode == 0 && TryParseItems(result.Stdout, out _);
        if (!listed || !TryParseItems(result.Stdout, out var items))
        {
            return pods;
        }

        foreach (var item in items)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item?["metadata"]?["labels"] is JsonObject labelObject)
            {
                foreach (var label in labelObject)
                {
                    labels[label.Key] = label.Value?.GetValue<string>() ?? string.Empty;
                }
            }

            var phase = item?["status"]?["phase"]?.GetValue<string>() ?? string.Empty;
            var readyCondition = (item?["status"]?["conditions"] as JsonArray)?
                .FirstOrDefault(condition => condition?["type"]?.GetValue<string>() == "Ready");
            var ready = phase == "Succeeded" || readyCondition?["status"]?.GetValue<string>() == "True";

            var containers = new List<string>();
            foreach (var group in new[] { "initContainers", "containers" })
            {
                if (item?["spec"]?[group] is JsonArray array)
                {
                    containers.AddRange(array.Select(container => container?["name"]?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0));
                }
            }

            pods.Add(new Pod(item?["metadata"]?["name"]?.GetValue<string>() ?? string.Empty, labels, ready, containers));
        }

        return pods;
    }

    /*
     * Blanks the values in the `Environment:` blocks of `kubectl describe pod` output.
     * Variable names stay (they say which setting is in play); a literal value may be a connection string.
     * References such as `<set to the key 'x' in secret 'y'>` are kept: they name a source, not a value.
     */
    internal static string RedactDescribe(string text)
    {
        var builder = new StringBuilder();
        var blockIndent = -1;
        foreach (var line in text.Split('\n'))
        {
            var indent = line.Length - line.TrimStart(' ').Length;
            if (blockIndent >= 0 && line.Trim().Length > 0 && indent <= blockIndent)
            {
                blockIndent = -1;
            }

            if (blockIndent >= 0)
            {
                var match = Regex.Match(line, @"^(\s+[^:\s]+:)(\s*)(.*)$");
                if (match.Success && match.Groups[3].Value.Length > 0 && !match.Groups[3].Value.StartsWith('<'))
                {
                    builder.Append(match.Groups[1].Value).Append(match.Groups[2].Value).Append("<redacted>").Append('\n');
                    continue;
                }
            }
            else if (Regex.IsMatch(line, @"^\s*Environment:\s*$"))
            {
                blockIndent = indent;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- schema rollback hook

    /*
     * Returns "<uid>/<generation>" for the schema-rollback CronJob, or empty when it does not exist.
     * Both parts are needed to tell whether a deployment replaced the CronJob, because charts ship it two ways:
     *   - as an ordinary chart resource: Helm patches it in place, so the uid stays and the generation rises;
     *   - as a Helm hook with `before-hook-creation`: Helm deletes and recreates it before every upgrade, so
     *     the uid changes while the generation is 1 again, the same value the previous release's object had.
     * Comparing the generation alone would call every hook-shaped CronJob "unchanged" and never run it.
     */
    static string ReadCronJobIdentity(Settings settings, string ns)
    {
        if (settings.SchemaRollbackCronJob.Length == 0)
        {
            return string.Empty;
        }

        var result = Run("kubectl", ["get", "cronjob", settings.SchemaRollbackCronJob, "--namespace", ns, "--ignore-not-found", "--output", "json"], QueryTimeout);
        if (result.ExitCode != 0)
        {
            // --ignore-not-found makes an absent CronJob exit 0, so a non-zero exit is a failed read, not absence.
            // It used to pass silently and looked exactly like "no schema hook".
            var stderr = string.Join(' ', result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Annotate("warning", $"Could not read the schema rollback CronJob (kubectl exited with code {Text(result.ExitCode)}; standard error: {(stderr.Length == 0 ? "empty" : stderr.Length > 600 ? stderr[..600] + "..." : stderr)}). It is treated as absent.");
            return string.Empty;
        }

        if (result.Stdout.Trim().Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var metadata = JsonNode.Parse(result.Stdout)?["metadata"];
            if (metadata is null)
            {
                return string.Empty;
            }

            var uid = metadata["uid"] is JsonValue uidValue && uidValue.TryGetValue<string>(out var uidText) ? uidText : string.Empty;
            return $"{uid}/{Text(Number(metadata["generation"]) ?? 0)}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    static SchemaJob? PrepareSchemaRollback(Settings settings, GuardState state, Report report, string ns)
    {
        var cronJob = settings.SchemaRollbackCronJob;
        if (cronJob.Length == 0)
        {
            report.SchemaRollback = SchemaOutcomes.NotConfigured;
            return null;
        }

        var identity = ReadCronJobIdentity(settings, ns);
        if (identity.Length == 0)
        {
            Console.WriteLine($"No CronJob '{cronJob}' in namespace '{ns}': the release ships no schema rollback hook, skipping.");
            report.SchemaRollback = SchemaOutcomes.NotFound;
            return null;
        }

        // An unchanged CronJob (same uid and same generation) is still the previous release's: running it would
        // undo the schema of the release that is being restored. Only a CronJob this deploy created, patched
        // (generation) or recreated (uid, the Helm hook shape) describes the failed release.
        if (!report.Adopted && identity == state.SchemaCronJobIdentity)
        {
            Console.WriteLine($"CronJob '{cronJob}' was not changed by this deployment, so it still belongs to the previous release. Not running it.");
            report.SchemaRollback = SchemaOutcomes.Skipped;
            report.SchemaRollbackDetail = "The CronJob was not changed by this deployment.";
            return null;
        }

        if (report.Adopted)
        {
            // Without a captured identity there is no telling whose CronJob this is; guessing could undo a good schema.
            Console.WriteLine($"CronJob '{cronJob}' exists, but its state before the deployment was not captured for this release. Not running it.");
            report.SchemaRollback = SchemaOutcomes.Skipped;
            report.SchemaRollbackDetail = "Release name mismatch: the CronJob's state before the deployment is unknown. Set helm-release-name.";
            return null;
        }

        var suffix = $"-{settings.RunId}-{settings.RunAttempt}";
        var jobName = cronJob[..Math.Min(cronJob.Length, 63 - suffix.Length)].TrimEnd('-', '.') + suffix;
        var rendered = Run("kubectl", ["create", "job", jobName, $"--from=cronjob/{cronJob}", "--namespace", ns, "--dry-run=client", "--output", "json"], QueryTimeout);
        JsonNode? job = null;
        if (rendered.ExitCode == 0)
        {
            try
            {
                job = JsonNode.Parse(rendered.Stdout);
            }
            catch (JsonException)
            {
                job = null;
            }
        }

        if (job is not JsonObject jobObject || jobObject["metadata"] is not JsonObject metadata || jobObject["spec"] is not JsonObject spec)
        {
            Annotate("error", $"SCHEMA ROLLBACK FAILED: could not render a Job from CronJob '{cronJob}'. {FirstLine(rendered.Stderr, string.Empty)}");
            report.SchemaRollback = SchemaOutcomes.Failed;
            report.SchemaRollbackDetail = "The Job could not be rendered from the CronJob.";
            return null;
        }

        // The owner reference points at the CronJob the rollback is about to revert or delete; a Job owned by a
        // deleted object is garbage-collected before it runs.
        // Refuse to run a hook that cannot tell this attempt's schema changes from an earlier deploy's.
        if (!DateTime.TryParseExact(state.DeployStartedAt, DeployStartedAtFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            || !InjectDeployStartedAt(spec, state.DeployStartedAt))
        {
            Annotate("error", $"SCHEMA ROLLBACK FAILED: could not pass {DeployStartedAtVariable} to the Job rendered from CronJob '{cronJob}'.");
            report.SchemaRollback = SchemaOutcomes.Failed;
            report.SchemaRollbackDetail = $"The Job has no containers to receive {DeployStartedAtVariable}, or the deployment start was not recorded.";
            return null;
        }

        metadata.Remove("ownerReferences");
        metadata.Remove("uid");
        metadata.Remove("resourceVersion");
        metadata.Remove("creationTimestamp");
        metadata["namespace"] = ns;
        if (metadata["labels"] is not JsonObject labels)
        {
            labels = new JsonObject();
            metadata["labels"] = labels;
        }

        labels["arkanis.dev/deploy-guard"] = "schema-rollback";

        // Backstop for a runner that dies before it can clean up.
        spec["ttlSecondsAfterFinished"] ??= SchemaJobTtlSeconds;

        var manifestDirectory = Path.Combine(settings.RunnerTemp, "deploy-guard");
        Directory.CreateDirectory(manifestDirectory);
        var manifestPath = Path.Combine(manifestDirectory, "schema-rollback-job.json");
        File.WriteAllText(manifestPath, jobObject.ToJsonString());
        Console.WriteLine($"Rendered schema rollback Job '{jobName}' from CronJob '{cronJob}' (uid/generation {(state.SchemaCronJobIdentity.Length == 0 ? "absent" : state.SchemaCronJobIdentity)} -> {identity}).");
        report.SchemaRollbackJob = jobName;
        return new SchemaJob(jobName, manifestPath);
    }

    /*
     * Sets DeployStartedAtVariable on every container and init container of the Job's pod template, replacing an
     * entry of that name (value or valueFrom) so the chart cannot shadow it. False when there is no container.
     */
    static bool InjectDeployStartedAt(JsonObject jobSpec, string startedAt)
    {
        if (jobSpec["template"]?["spec"] is not JsonObject podSpec)
        {
            return false;
        }

        var injected = 0;
        foreach (var group in new[] { "initContainers", "containers" })
        {
            if (podSpec[group] is not JsonArray containers)
            {
                continue;
            }

            foreach (var container in containers.OfType<JsonObject>())
            {
                if (container["env"] is not JsonArray env)
                {
                    env = new JsonArray();
                    container["env"] = env;
                }

                for (var index = env.Count - 1; index >= 0; index--)
                {
                    if (env[index]?["name"] is JsonValue name && name.TryGetValue<string>(out var text) && text == DeployStartedAtVariable)
                    {
                        env.RemoveAt(index);
                    }
                }

                env.Add(new JsonObject { ["name"] = DeployStartedAtVariable, ["value"] = startedAt });
                injected++;
            }
        }

        return injected > 0 && podSpec["containers"] is JsonArray { Count: > 0 };
    }

    static void RunSchemaRollback(Settings settings, Report report, SchemaJob job, string ns)
    {
        Console.WriteLine($"::group::Schema rollback Job {job.Name}");
        try
        {
            var create = Run("kubectl", ["create", "--filename", job.ManifestPath, "--namespace", ns], QueryTimeout, echoOutput: true);
            if (create.ExitCode != 0)
            {
                report.SchemaRollback = SchemaOutcomes.Failed;
                report.SchemaRollbackDetail = "The Job could not be created.";
                return;
            }

            var result = WaitForJob(settings, job.Name, ns);
            var directory = Path.Combine(settings.ReportDirectory, "diagnostics", "schema-rollback");
            Directory.CreateDirectory(directory);
            // Echoed and summarised, not only saved: a completed Job is deleted below, so the artifact would otherwise
            // be the only place that says what the hook did (reverted something, or found nothing to revert).
            var logs = Run("kubectl", ["logs", $"job/{job.Name}", "--namespace", ns, "--all-containers", "--tail=1000", "--timestamps"], QueryTimeout, echoOutput: true);
            Save(directory, "job-logs.txt", logs);
            report.SchemaRollbackOutput = LastLogLines(logs.Stdout, SchemaJobSummaryLines);
            Save(directory, "job.txt", Run("kubectl", ["get", "job", job.Name, "--namespace", ns, "--output", "wide"], QueryTimeout));

            report.SchemaRollback = result;
            if (result == SchemaOutcomes.Succeeded)
            {
                Console.WriteLine("Schema rollback Job completed.");
                Run("kubectl", ["delete", "job", job.Name, "--namespace", ns, "--ignore-not-found", "--wait=false"], QueryTimeout, echoOutput: true);
                return;
            }

            // Left in place on purpose: a Job still running must not be killed mid-migration, and a failed one is
            // evidence. Its ttlSecondsAfterFinished removes it later.
            report.SchemaRollbackDetail = result == SchemaOutcomes.TimedOut
                ? $"Job {job.Name} did not finish within {settings.RolloutTimeoutText} and is still in the cluster."
                : $"Job {job.Name} failed and is left in the cluster for inspection.";
        }
        finally
        {
            Console.WriteLine("::endgroup::");
            if (report.SchemaRollback is SchemaOutcomes.Failed or SchemaOutcomes.TimedOut)
            {
                Annotate("error", $"SCHEMA ROLLBACK FAILED: {report.SchemaRollbackDetail} Workloads are restored, the database schema is not.");
            }
        }
    }

    /**
     * The last `count` non-empty lines of a `kubectl logs --timestamps` capture, without the timestamp kubectl put in
     * front of each. A hook says what it did at the end of its log; everything before is usually SQL noise.
     */
    static string LastLogLines(string log, int count)
    {
        var lines = log.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                var isStamp = space > 0 && DateTimeOffset.TryParse(line[..space], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                return (isStamp ? line[(space + 1)..] : line).TrimEnd();
            })
            .Where(line => line.Trim().Length > 0)
            .ToList();
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - count)));
    }

    static string WaitForJob(Settings settings, string jobName, string ns)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var result = Run("kubectl", ["get", "job", jobName, "--namespace", ns, "--output", "json"], QueryTimeout, quiet: true);
            if (result.ExitCode == 0)
            {
                try
                {
                    if (JsonNode.Parse(result.Stdout)?["status"]?["conditions"] is JsonArray conditions)
                    {
                        bool Has(string type) => conditions.Any(condition =>
                            condition?["type"]?.GetValue<string>() == type && condition["status"]?.GetValue<string>() == "True");
                        if (Has("Complete"))
                        {
                            return SchemaOutcomes.Succeeded;
                        }

                        if (Has("Failed"))
                        {
                            return SchemaOutcomes.Failed;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Treated like a failed poll: try again until the deadline.
                }
            }

            if (elapsed.Elapsed >= settings.RolloutTimeout)
            {
                return SchemaOutcomes.TimedOut;
            }

            Thread.Sleep(settings.PollInterval);
        }
    }

    // ---------------------------------------------------------------- reporting

    static int Publish(Settings settings, Report report)
    {
        var failedResources = string.Join(",", report.FailedResources);
        WriteOutput(settings, "helm-release", report.Release);
        WriteOutput(settings, "previous-revision", Text(report.PreviousRevision));
        WriteOutput(settings, "new-revision", Text(report.NewRevision));
        WriteOutput(settings, "current-revision", Text(report.CurrentRevision));
        WriteOutput(settings, "outcome", report.Outcome);
        WriteOutput(settings, "schema-rollback", report.SchemaRollback);
        WriteOutput(settings, "failed-resources", failedResources);
        WriteOutput(settings, "downtime-started-at", report.Exclusive.DowntimeStartedAt);
        WriteOutput(settings, "downtime-ended-at", report.Exclusive.DowntimeEndedAt);

        var headline = report.Outcome switch
        {
            Outcomes.Deployed => $"Deployed revision {Text(report.CurrentRevision)}.",
            Outcomes.RolledBack => $"DEPLOYMENT FAILED - rolled back to revision {Text(report.RollbackTargetRevision)}, the previous release is live.",
            Outcomes.RollbackFailed when report.HelmRollbackSucceeded && report.SchemaRollback is SchemaOutcomes.Failed or SchemaOutcomes.TimedOut =>
                $"ROLLBACK FAILED, environment needs attention - workloads restored to revision {Text(report.RollbackTargetRevision)}, SCHEMA ROLLBACK FAILED.",
            Outcomes.RollbackFailed => "ROLLBACK FAILED, environment needs attention.",
            Outcomes.FailedNoPreviousRelease => "DEPLOYMENT FAILED - first install, no previous release to restore. Environment needs attention.",
            Outcomes.FailedRollbackDisabled => "DEPLOYMENT FAILED - rollback-on-failure is false, nothing was restored. Environment needs attention.",
            Outcomes.FailedExclusiveStop => "DEPLOYMENT NOT STARTED - the release could not be stopped for the exclusive deployment. Replica counts restored.",
            Outcomes.FailedReleaseUnchanged => "DEPLOYMENT FAILED before Helm changed the release - environment untouched.",
            Outcomes.Cancelled => "DEPLOYMENT CANCELLED - release state not verified.",
            _ => "Deployment did not start - environment untouched.",
        };

        var summary = new StringBuilder();
        summary.Append("## Helm release guard\n\n");
        summary.Append("**").Append(headline).Append("**\n\n");
        summary.Append(report.Detail).Append("\n\n");
        if (report.FailureReason.Length > 0)
        {
            // Indented code block: the error is Helm's text and may itself contain backticks or fences.
            summary.Append("**Why it failed**\n\n");
            foreach (var line in report.FailureReason.ReplaceLineEndings("\n").Split('\n'))
            {
                summary.Append("    ").Append(line).Append('\n');
            }

            summary.Append('\n');
        }

        if (report.SchemaRollbackOutput.Length > 0)
        {
            // Indented code block for the same reason as above: this is the consumer's own log text.
            summary.Append("**What the schema rollback Job reported** (last lines of its log)\n\n");
            foreach (var line in report.SchemaRollbackOutput.Split('\n'))
            {
                summary.Append("    ").Append(line).Append('\n');
            }

            summary.Append('\n');
        }

        var downtime = DowntimeSentence(report.Exclusive);
        if (downtime.Length > 0)
        {
            summary.Append("**").Append(downtime).Append("**\n\n");
        }

        summary.Append("| Field | Value |\n| --- | --- |\n");
        void Row(string field, string value) => summary.Append("| ").Append(field).Append(" | `").Append(value.Length == 0 ? "n/a" : value).Append("` |\n");
        Row("Outcome", report.Outcome);
        Row("Helm release", report.Release);
        Row("Namespace", report.Namespace);
        Row("Revision before this deployment", RevisionBefore(report));
        Row("Revision this deployment created", RevisionCreated(report));
        Row("Revision live now", RevisionLive(report));
        Row("Schema rollback", report.SchemaRollbackDetail.Length == 0 ? report.SchemaRollback : $"{report.SchemaRollback}: {report.SchemaRollbackDetail}");
        if (report.Outcome == Outcomes.Deployed)
        {
            Row("Rollout verified by the guard", report.RolloutVerified ? "yes" : "no, Helm's wait only");
        }

        Row("Failed resources", string.Join(", ", report.FailedResources));
        Row("Stuck release recovery", report.Recovery);
        Row("Rollout timeout", report.RolloutTimeout);
        Row("Deploy mode", report.Exclusive.Enabled ? $"exclusive ({report.Exclusive.Status})" : "rolling");
        if (report.Exclusive.Enabled)
        {
            Row("Stopped replicas", string.Join(", ", report.Exclusive.Workloads.Select(workload => $"{workload.Name}={Text(workload.Replicas)}")));
            Row("Downtime", report.Exclusive.DowntimeStartedAt.Length == 0
                ? string.Empty
                : $"{report.Exclusive.DowntimeStartedAt} to {(report.Exclusive.DowntimeEndedAt.Length == 0 ? "not restored" : report.Exclusive.DowntimeEndedAt)}");
            Row("Replicas restored by the guard", string.Join(", ", report.Exclusive.RestoredByGuard));
        }
        if (report.Outcome == Outcomes.Deployed && report.PreviousRevision is not null)
        {
            summary.Append("\nManual rollback reference: `helm rollback ").Append(report.Release).Append(' ').Append(Text(report.PreviousRevision))
                .Append(" --namespace ").Append(report.Namespace).Append(" --wait`\n");
        }

        File.WriteAllText(Path.Combine(settings.ReportDirectory, "deploy-report.md"), summary.ToString());
        if (settings.StepSummaryPath.Length > 0)
        {
            File.AppendAllText(settings.StepSummaryPath, summary.ToString());
        }

        var json = new JsonObject
        {
            ["schema"] = "ci-deploy-report/v1",
            ["outcome"] = report.Outcome,
            ["detail"] = report.Detail,
            ["failure_reason"] = report.FailureReason,
            ["helm_release"] = report.Release,
            ["namespace"] = report.Namespace,
            ["deploy_step_outcome"] = report.DeployStepOutcome,
            ["previous_revision"] = report.PreviousRevision,
            ["previous_status"] = report.PreviousStatus,
            ["new_revision"] = report.NewRevision,
            ["current_revision"] = report.CurrentRevision,
            ["current_status"] = report.CurrentStatus,
            ["rollback_target_revision"] = report.RollbackTargetRevision,
            ["helm_rollback_succeeded"] = report.HelmRollbackSucceeded,
            ["schema_rollback"] = report.SchemaRollback,
            ["schema_rollback_detail"] = report.SchemaRollbackDetail,
            ["schema_rollback_job"] = report.SchemaRollbackJob,
            ["schema_rollback_output"] = report.SchemaRollbackOutput,
            ["stuck_release_recovery"] = report.Recovery,
            ["release_name_adopted_from_deploy"] = report.Adopted,
            ["verification_failed_after_helm_success"] = report.VerificationFailedAfterHelmSuccess,
            ["rollout_verified"] = report.RolloutVerified,
            ["rollout_timeout"] = report.RolloutTimeout,
            ["failed_resources"] = new JsonArray([.. report.FailedResources.Select(name => (JsonNode?)JsonValue.Create(name))]),
            ["exclusive"] = report.Exclusive.ToJson(),
        };
        File.WriteAllText(Path.Combine(settings.ReportDirectory, "deploy-report.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Console.WriteLine(headline);
        if (downtime.Length > 0)
        {
            Console.WriteLine(downtime);
        }

        if (report.Outcome is not (Outcomes.Deployed or Outcomes.NotAttempted))
        {
            Annotate("error", headline);
            if (report.FailureReason.Length > 0)
            {
                Annotate("error", $"Cause: {report.FailureReason}");
            }
        }

        // A job that already failed stays failed. This exit code is what fails it when Helm said "success" but the
        // rollout verification disagreed, and it keeps every non-deployed outcome red in its own right.
        return report.Outcome is Outcomes.Deployed or Outcomes.NotAttempted ? 0 : 1;
    }

    /*
     * The three revision rows of the summary. Helm's status words are spelled out in plain language on purpose:
     * a bare "(deployed)" next to a revision reads like a verdict on this run, while it only says that the
     * revision is the one Helm currently counts as installed.
     */
    static string RevisionBefore(Report report) => report.PreviousRevision is null
        ? "none (first install)"
        : report.PreviousStatus is "deployed" or ""
            ? Text(report.PreviousRevision)
            : $"{Text(report.PreviousRevision)} (was already in Helm status '{report.PreviousStatus}')";

    static string RevisionCreated(Report report) => report.NewRevision is null
        ? "none (Helm did not get that far)"
        : report.Outcome == Outcomes.Deployed
            ? $"{Text(report.NewRevision)} (succeeded)"
            : report.VerificationFailedAfterHelmSuccess
                ? $"{Text(report.NewRevision)} (Helm succeeded, rollout did not)"
                : $"{Text(report.NewRevision)} (failed)";

    static string RevisionLive(Report report)
    {
        if (report.CurrentRevision is null)
        {
            return "unknown";
        }

        var current = Text(report.CurrentRevision);
        if (report.HelmRollbackSucceeded && report.NewRevision is not null && report.RollbackTargetRevision is not null)
        {
            // Helm records a rollback as a new revision with the old content, so the number alone misleads.
            return $"{current} (rolled back from {Text(report.NewRevision)} to {Text(report.RollbackTargetRevision)}: same content as revision {Text(report.RollbackTargetRevision)})";
        }

        if (report.Outcome == Outcomes.Deployed)
        {
            return $"{current} (this deployment)";
        }

        if (report.CurrentRevision == report.PreviousRevision)
        {
            return $"{current} (unchanged)";
        }

        return report.CurrentStatus.Length == 0 ? current : $"{current} (NOT restored, Helm status '{report.CurrentStatus}')";
    }

    // Plain statement of the outage an exclusive deployment caused; empty for a rolling one or when nothing was stopped.
    static string DowntimeSentence(ExclusiveState exclusive)
    {
        if (!exclusive.Enabled)
        {
            return string.Empty;
        }

        if (exclusive.DowntimeStartedAt.Length == 0)
        {
            return exclusive.Status switch
            {
                ExclusiveStatuses.FirstInstall => "This deployment was exclusive. It was a first install, so nothing was stopped and there was no downtime.",
                ExclusiveStatuses.NoDeployments => "This deployment was exclusive, but the release owned no Deployment, so nothing was stopped.",
                _ => "This deployment was exclusive. Nothing was stopped.",
            };
        }

        return exclusive.DowntimeEndedAt.Length > 0
            ? $"This deployment was exclusive: the service was down from {exclusive.DowntimeStartedAt} to {exclusive.DowntimeEndedAt}."
            : $"This deployment was exclusive: the service has been down since {exclusive.DowntimeStartedAt} and is NOT restored.";
    }

    static void WriteOutput(Settings settings, string name, string value)
    {
        if (settings.OutputPath.Length > 0)
        {
            File.AppendAllText(settings.OutputPath, $"{name}={value.ReplaceLineEndings(" ")}\n");
        }
    }

    static void Save(string directory, string fileName, ProcessResult result) =>
        File.WriteAllText(Path.Combine(directory, fileName), result.Stdout + result.Stderr);

    static void SaveState(Settings settings, GuardState state)
    {
        var json = new JsonObject
        {
            ["release"] = state.Release,
            ["namespace"] = state.Namespace,
            ["captured"] = state.Captured,
            ["recovery"] = state.Recovery,
            ["recovered_from_status"] = state.RecoveredFromStatus,
            ["schema_cronjob_identity"] = state.SchemaCronJobIdentity,
            ["deploy_started_at"] = state.DeployStartedAt,
            ["exclusive"] = state.Exclusive.ToJson(),
            ["history"] = HistoryEntry.ToJson(state.History),
        };
        File.WriteAllText(settings.StatePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    static GuardState? LoadState(Settings settings)
    {
        if (!File.Exists(settings.StatePath))
        {
            return null;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(settings.StatePath));
            return new GuardState
            {
                Release = json?["release"]?.GetValue<string>() ?? settings.ReleaseName,
                Namespace = json?["namespace"]?.GetValue<string>() ?? settings.Namespace,
                Captured = json?["captured"]?.GetValue<bool>() ?? false,
                Recovery = json?["recovery"]?.GetValue<string>() ?? "none",
                RecoveredFromStatus = json?["recovered_from_status"]?.GetValue<string>() ?? string.Empty,
                SchemaCronJobIdentity = json?["schema_cronjob_identity"]?.GetValue<string>() ?? string.Empty,
                DeployStartedAt = json?["deploy_started_at"]?.GetValue<string>() ?? string.Empty,
                Exclusive = ExclusiveState.FromJson(json?["exclusive"]),
                History = HistoryEntry.Parse(json?["history"]?.ToJsonString() ?? "[]") ?? [],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- plumbing

    static ProcessResult Run(string fileName, string[] arguments, TimeSpan timeout, bool echoOutput = false, bool quiet = false)
    {
        if (!quiet)
        {
            Console.WriteLine($"$ {fileName} {string.Join(' ', arguments)}");
        }

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
                var message = $"{fileName} {arguments[0]} exceeded its {Text((int)timeout.TotalSeconds)}s limit and was stopped.";
                Console.WriteLine(message);
                return new ProcessResult(124, string.Empty, message + "\n");
            }

            process.WaitForExit();
            var result = new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            if (echoOutput)
            {
                Console.Write(result.Stdout);
                Console.Write(result.Stderr);
            }

            return result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var message = $"Could not run {fileName}: {ex.Message}";
            Console.WriteLine(message);
            return new ProcessResult(127, string.Empty, message + "\n");
        }
    }

    static bool TryParseArray(string text, out JsonArray array)
    {
        array = new JsonArray();
        if (text.Trim().Length == 0)
        {
            return true;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonArray parsed)
            {
                array = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // Reported by the caller.
        }

        return false;
    }

    static bool TryParseItems(string text, out JsonArray items)
    {
        items = new JsonArray();
        try
        {
            if (JsonNode.Parse(text)?["items"] is JsonArray parsed)
            {
                items = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // Reported by the caller.
        }

        return false;
    }

    internal static long? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0);
        return line ?? fallback;
    }

    static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    internal static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    static void Annotate(string level, string message) =>
        Console.WriteLine($"::{level}::{message.ReplaceLineEndings(" ")}");
}

static class Outcomes
{
    public const string Deployed = "deployed";
    public const string RolledBack = "rolled-back";
    public const string RollbackFailed = "rollback-failed";
    public const string FailedNoPreviousRelease = "failed-no-previous-release";
    public const string FailedReleaseUnchanged = "failed-release-unchanged";
    public const string FailedRollbackDisabled = "failed-rollback-disabled";
    public const string FailedExclusiveStop = "failed-exclusive-stop";
    public const string Cancelled = "cancelled";
    public const string NotAttempted = "not-attempted";
}

static class SchemaOutcomes
{
    public const string NotRun = "not-run";
    public const string NotConfigured = "not-configured";
    public const string NotFound = "not-found";
    public const string Skipped = "skipped";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed-out";
}

sealed class GuardInputException(string message) : Exception(message);

sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

static class ExclusiveStatuses
{
    public const string Off = "off";
    public const string FirstInstall = "first-install";
    public const string NoDeployments = "no-deployments";
    public const string Stopped = "stopped";
    public const string StopFailed = "stop-failed";
}

static class ExclusiveRestores
{
    public const string NotRun = "not-run";
    public const string NotNeeded = "not-needed";
    public const string Restored = "restored";
    public const string Failed = "failed";
}

// A Deployment an exclusive deployment stopped: its name, the replica count to put back, and the selector its pods carry.
sealed record ExclusiveWorkload(string Name, long Replicas, Dictionary<string, string> Selector);

/*
 * What an exclusive deployment stopped and when. Written to the state file by the capture phase before the first
 * scale call, so the finalize phase can put the replica counts back whatever happens in between.
 */
sealed class ExclusiveState
{
    public bool Enabled { get; set; }

    // off | first-install | no-deployments | stopped | stop-failed
    public string Status { get; set; } = ExclusiveStatuses.Off;

    public List<ExclusiveWorkload> Workloads { get; set; } = [];

    // RFC 3339 UTC second the first Deployment was scaled to 0; empty when nothing was stopped.
    public string DowntimeStartedAt { get; set; } = string.Empty;

    // RFC 3339 UTC second the restored or newly deployed release was verified rolled out; empty while still down.
    public string DowntimeEndedAt { get; set; } = string.Empty;

    // not-run | not-needed | restored | failed
    public string Restore { get; set; } = ExclusiveRestores.NotRun;

    // Deployments Helm did not bring back by itself, so the guard scaled them.
    public List<string> RestoredByGuard { get; set; } = [];

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["status"] = Status,
        ["downtime_started_at"] = DowntimeStartedAt,
        ["downtime_ended_at"] = DowntimeEndedAt,
        ["restore"] = Restore,
        ["restored_by_guard"] = new JsonArray([.. RestoredByGuard.Select(name => (JsonNode?)JsonValue.Create(name))]),
        ["workloads"] = new JsonArray([.. Workloads.Select(workload => (JsonNode?)new JsonObject
        {
            ["name"] = workload.Name,
            ["replicas"] = workload.Replicas,
            ["selector"] = new JsonObject([.. workload.Selector.Select(label => KeyValuePair.Create(label.Key, (JsonNode?)JsonValue.Create(label.Value)))]),
        })]),
    };

    public static ExclusiveState FromJson(JsonNode? json)
    {
        var state = new ExclusiveState();
        if (json is not JsonObject)
        {
            return state;
        }

        state.Enabled = json["enabled"]?.GetValue<bool>() ?? false;
        state.Status = json["status"]?.GetValue<string>() ?? ExclusiveStatuses.Off;
        state.DowntimeStartedAt = json["downtime_started_at"]?.GetValue<string>() ?? string.Empty;
        state.DowntimeEndedAt = json["downtime_ended_at"]?.GetValue<string>() ?? string.Empty;
        state.Restore = json["restore"]?.GetValue<string>() ?? ExclusiveRestores.NotRun;
        state.RestoredByGuard = [.. (json["restored_by_guard"] as JsonArray ?? []).Select(name => name?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0)];
        foreach (var item in json["workloads"] as JsonArray ?? [])
        {
            var selector = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item?["selector"] is JsonObject labels)
            {
                foreach (var label in labels)
                {
                    selector[label.Key] = label.Value?.GetValue<string>() ?? string.Empty;
                }
            }

            state.Workloads.Add(new ExclusiveWorkload(item?["name"]?.GetValue<string>() ?? string.Empty, Guard.Number(item?["replicas"]) ?? 1, selector));
        }

        return state;
    }
}

sealed record Workload(string Kind, string Name, Dictionary<string, string> Selector, bool RolledOut, long Replicas)
{
    public bool Selects(Pod pod) =>
        Selector.Count > 0 && Selector.All(label => pod.Labels.TryGetValue(label.Key, out var value) && value == label.Value);
}

sealed record Pod(string Name, Dictionary<string, string> Labels, bool Ready, List<string> Containers);

sealed record SchemaJob(string Name, string ManifestPath);

sealed record HistoryEntry(int Revision, string Status, string Description)
{
    public static List<HistoryEntry>? Parse(string json)
    {
        if (json.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return null;
            }

            var entries = new List<HistoryEntry>();
            foreach (var item in array)
            {
                var revision = Guard.Number(item?["revision"]);
                if (revision is null)
                {
                    continue;
                }

                entries.Add(new HistoryEntry(
                    (int)revision.Value,
                    item?["status"]?.GetValue<string>() ?? string.Empty,
                    item?["description"]?.GetValue<string>() ?? string.Empty));
            }

            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonArray ToJson(List<HistoryEntry> history) =>
        new([.. history.Select(entry => (JsonNode?)new JsonObject
        {
            ["revision"] = entry.Revision,
            ["status"] = entry.Status,
            ["description"] = entry.Description,
        })]);
}

sealed class GuardState
{
    public string Release { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    // False until the capture phase finished: finalize then knows the deploy was never allowed to start.
    public bool Captured { get; set; }

    // none | recovered | failed | impossible | unreadable
    public string Recovery { get; set; } = "none";

    public string RecoveredFromStatus { get; set; } = string.Empty;

    // "<uid>/<generation>" of the schema-rollback CronJob before the deploy; empty when it did not exist.
    public string SchemaCronJobIdentity { get; set; } = string.Empty;

    // RFC 3339 UTC second at which the capture phase began; handed to the schema rollback Job.
    public string DeployStartedAt { get; set; } = string.Empty;

    public ExclusiveState Exclusive { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = [];
}

sealed class Report
{
    public string Outcome { get; set; } = Outcomes.NotAttempted;

    public string Detail { get; set; } = string.Empty;

    // Helm's own error for the revision this deployment created, or the guard's reason when Helm reported success.
    public string FailureReason { get; set; } = string.Empty;

    public string Release { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string DeployStepOutcome { get; set; } = string.Empty;

    public int? PreviousRevision { get; set; }

    public string PreviousStatus { get; set; } = string.Empty;

    public int? NewRevision { get; set; }

    public int? CurrentRevision { get; set; }

    public string CurrentStatus { get; set; } = string.Empty;

    public int? RollbackTargetRevision { get; set; }

    public bool HelmRollbackSucceeded { get; set; }

    public string SchemaRollback { get; set; } = SchemaOutcomes.NotRun;

    public string SchemaRollbackDetail { get; set; } = string.Empty;

    public string SchemaRollbackJob { get; set; } = string.Empty;

    /** Closing lines of the schema rollback Job's log: what the consumer's hook said it did. Empty when it never ran. */
    public string SchemaRollbackOutput { get; set; } = string.Empty;

    public string Recovery { get; set; } = "none";

    public bool Adopted { get; set; }

    public bool VerificationFailedAfterHelmSuccess { get; set; }

    // True only when the guard itself watched at least one Deployment of the release roll out after a successful deploy.
    // False does not mean failure: Helm's --wait may have been the only check (see the detail text for why).
    public bool RolloutVerified { get; set; }

    // Exclusive only: a schema Job was due but could not run safely, so the outcome cannot be a clean rollback.
    public bool SchemaRollbackBlocked { get; set; }

    public ExclusiveState Exclusive { get; set; } = new();

    public string RolloutTimeout { get; set; } = string.Empty;

    public List<string> FailedResources { get; } = [];
}

/*
 * What the helm shim saw `aspire deploy` do: the release and namespace it really targeted, and that release's
 * history immediately before the upgrade. Used when the AppHost names the release differently than assumed.
 */
sealed record ShimRecord(string Release, string Namespace, List<HistoryEntry> HistoryBefore)
{
    public static ShimRecord? TryRead(string recordPath)
    {
        if (recordPath.Length == 0 || !File.Exists(recordPath))
        {
            return null;
        }

        var values = File.ReadAllLines(recordPath)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("release", out var release) || release.Length == 0
            || !values.TryGetValue("namespace", out var ns) || ns.Length == 0)
        {
            return null;
        }

        var historyPath = recordPath + ".history.json";
        var history = File.Exists(historyPath) ? HistoryEntry.Parse(File.ReadAllText(historyPath)) : null;
        return new ShimRecord(release, ns, history ?? []);
    }
}

sealed record Settings(
    string ReleaseName,
    string Namespace,
    string RolloutTimeoutText,
    TimeSpan RolloutTimeout,
    bool RollbackOnFailure,
    bool Exclusive,
    string SchemaRollbackCronJob,
    string ReportDirectory,
    string StatePath,
    string ShimRecordPath,
    string DeployOutcome,
    int JobTimeoutMinutes,
    TimeSpan PollInterval,
    string RunnerTemp,
    string RunId,
    string RunAttempt,
    string OutputPath,
    string StepSummaryPath)
{
    public static Settings FromEnvironment()
    {
        var ns = Required("DEPLOY_GUARD_NAMESPACE");
        if (!Regex.IsMatch(ns, "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$") || ns.Length > 63)
        {
            throw new GuardInputException($"Invalid Kubernetes namespace: {ns}");
        }

        var explicitRelease = Optional("DEPLOY_GUARD_RELEASE_NAME");
        var release = explicitRelease.Length > 0 ? explicitRelease : Required("DEPLOY_GUARD_ASPIRE_ENVIRONMENT").ToLowerInvariant();
        if (!Regex.IsMatch(release, @"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$") || release.Length > 53)
        {
            throw new GuardInputException(explicitRelease.Length > 0
                ? $"helm-release-name '{release}' is not a valid Helm release name."
                : $"The default Helm release name '{release}' (lowercased aspire-environment) is not a valid Helm release name; set helm-release-name.");
        }

        var cronJob = Optional("DEPLOY_GUARD_SCHEMA_ROLLBACK_CRONJOB");
        if (cronJob.Length > 0 && (!Regex.IsMatch(cronJob, @"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$") || cronJob.Length > 52))
        {
            throw new GuardInputException($"schema-rollback-cronjob '{cronJob}' is not a valid CronJob name.");
        }

        var timeoutText = Optional("DEPLOY_GUARD_ROLLOUT_TIMEOUT");
        if (timeoutText.Length == 0)
        {
            timeoutText = "15m";
        }

        var runnerTemp = Optional("RUNNER_TEMP");
        if (runnerTemp.Length == 0)
        {
            runnerTemp = Path.GetTempPath();
        }

        var workspace = Optional("GITHUB_WORKSPACE");
        var reportDirectory = Path.GetFullPath(Required("DEPLOY_GUARD_REPORT_DIR"), workspace.Length > 0 ? workspace : Directory.GetCurrentDirectory());
        var pollSeconds = double.TryParse(Optional("DEPLOY_GUARD_POLL_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPoll) ? parsedPoll : 5;
        _ = int.TryParse(Optional("DEPLOY_GUARD_JOB_TIMEOUT_MINUTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var jobTimeout);

        return new Settings(
            ReleaseName: release,
            Namespace: ns,
            RolloutTimeoutText: timeoutText,
            RolloutTimeout: ParseDuration(timeoutText),
            RollbackOnFailure: !string.Equals(Optional("DEPLOY_GUARD_ROLLBACK_ON_FAILURE"), "false", StringComparison.OrdinalIgnoreCase),

            // Opt-in, unlike the rollback: only an explicit "true" accepts the downtime.
            Exclusive: string.Equals(Optional("DEPLOY_GUARD_EXCLUSIVE"), "true", StringComparison.OrdinalIgnoreCase),
            SchemaRollbackCronJob: cronJob,
            ReportDirectory: reportDirectory,
            StatePath: Path.Combine(reportDirectory, "release-state.json"),
            ShimRecordPath: Optional("DEPLOY_GUARD_SHIM_RECORD"),
            DeployOutcome: Optional("DEPLOY_GUARD_DEPLOY_OUTCOME"),
            JobTimeoutMinutes: jobTimeout,
            PollInterval: TimeSpan.FromSeconds(Math.Max(0, pollSeconds)),
            RunnerTemp: runnerTemp,
            RunId: Sanitize(Optional("GITHUB_RUN_ID"), "local"),
            RunAttempt: Sanitize(Optional("GITHUB_RUN_ATTEMPT"), "1"),
            OutputPath: Optional("GITHUB_OUTPUT"),
            StepSummaryPath: Optional("GITHUB_STEP_SUMMARY"));
    }

    /*
     * Parses the subset of Go duration syntax Helm accepts for --timeout that makes sense here: whole or decimal
     * hours, minutes and seconds ("15m", "1h30m", "900s"). The same text is handed to Helm unchanged, so anything
     * this rejects is rejected before the deployment instead of by Helm halfway through it.
     */
    internal static TimeSpan ParseDuration(string text)
    {
        var match = Regex.Match(text, @"^(?:(\d+(?:\.\d+)?)(h|m|s))+$");
        if (!match.Success)
        {
            throw new GuardInputException($"rollout-timeout '{text}' is not a duration such as 15m, 900s or 1h30m.");
        }

        var total = TimeSpan.Zero;
        foreach (Match part in Regex.Matches(text, @"(\d+(?:\.\d+)?)(h|m|s)"))
        {
            var amount = double.Parse(part.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }

        if (total < TimeSpan.FromSeconds(1) || total > TimeSpan.FromHours(6))
        {
            throw new GuardInputException($"rollout-timeout '{text}' must be between 1s and 6h.");
        }

        return total;
    }

    static string Required(string name)
    {
        var value = Optional(name);
        return value.Length > 0 ? value : throw new GuardInputException($"{name} is required.");
    }

    static string Optional(string name) => (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();

    static string Sanitize(string value, string fallback)
    {
        var cleaned = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}
