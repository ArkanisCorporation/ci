#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;

/*
 * Summary:
 *   Exercises the helm-release-guard decision logic and the helm timeout shim without a cluster.
 *
 * Remarks:
 *   Run from the repository root with `dotnet run --file tests/helm-release-guard.cs --no-launch-profile`.
 *   A fake `helm` and `kubectl` (one Bash script under two names) sit first on PATH. They replay canned output
 *   from a per-scenario directory and append every invocation to calls.log, so each scenario can assert both what
 *   the guard decided and the order in which it touched the cluster. Linux and macOS only, like the shim.
 */

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("Helm release guard behavior skipped: the fake CLIs and the shim are Bash scripts.");
    return 0;
}

var repoRoot = Directory.GetCurrentDirectory();
var guardScript = Path.Combine(repoRoot, ".github", "actions", "helm-release-guard", "helm-release-guard.cs");
var shimScript = Path.Combine(repoRoot, ".github", "actions", "helm-release-guard", "helm-timeout-shim.sh");
var testRoot = Path.Combine(Path.GetTempPath(), "ci-helm-release-guard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

const string Release = "kubernetes-production";
const string SecretLiteral = "Host=db;Password=fixture-not-a-real-secret";

try
{
    // ---- Success: verified, previous and new revision recorded, nothing rolled back.
    {
        var s = NewScenario("success");
        s.History((3, "deployed"));
        var capture = s.Guard("capture");
        Check(capture.ExitCode == 0, "capture must succeed for a healthy release", capture.Log);
        Check(capture.Output("previous-revision") == "3" && capture.Output("helm-release") == Release, "capture must record the current revision");
        Check(capture.Output("exclusive") == "false", "a rolling deployment must not tell the deploy step it is exclusive", capture.Output("exclusive"));

        s.History((3, "superseded"), (4, "deployed"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0, "a verified deployment must end green", result.Log);
        Check(result.Output("outcome") == "deployed", "outcome must be deployed");
        Check(result.Output("previous-revision") == "3" && result.Output("new-revision") == "4" && result.Output("current-revision") == "4",
            "success must report previous and new revision");
        Check(s.Called("kubectl rollout status deployment/web"), "success must still verify every Deployment rollout");
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!["rollout_verified"]!.GetValue<bool>(),
            "a verified deployment must be recorded as verified");
        Check(!s.Called("helm rollback"), "success must not roll back");
        Check(result.Summary.Contains($"helm rollback {Release} 3 --namespace example-production --wait", StringComparison.Ordinal),
            "the summary must carry the manual rollback reference");
        Check(File.Exists(Path.Combine(s.ReportDir, "deploy-report.json")), "the report must be written to the artifact directory");
        Check(!s.Called("kubectl scale"), "a rolling deployment must never scale a Deployment", s.Calls);
        Check(!result.Summary.Contains("exclusive", StringComparison.Ordinal) && result.Output("downtime-started-at").Length == 0,
            "a rolling deployment must report no downtime", result.Summary);

        // Backwards compatible: a chart without the schema-confirm CronJob costs one read and changes nothing else.
        Check(result.Output("schema-confirm") == "not-found" && result.Summary.Contains("| Schema confirm | `not-found` |", StringComparison.Ordinal),
            "a release without the schema confirm CronJob must report not-found", result.Summary);
        Check(s.Called("kubectl get cronjob schema-confirm --namespace example-production --ignore-not-found --output json")
            && !s.Called("replicasets,cronjobs,jobs,pods") && !s.Called("kubectl create --filename"),
            "without the CronJob the guard must neither inspect the namespace nor create a Job", s.Calls);
        Check(!result.Log.Contains("SCHEMA CONFIRM", StringComparison.Ordinal), "a missing confirm CronJob must not warn", result.Log);
    }

    // ---- First install fails: nothing to restore, diagnostics still collected, no secret material stored.
    {
        var s = NewScenario("first-install-failure");
        var capture = s.Guard("capture");
        Check(capture.ExitCode == 0 && capture.Output("previous-revision").Length == 0, "capture must accept a missing release", capture.Log);

        s.History((1, "failed"));
        s.BreakDeployment("web");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0, "a failed first install must end red");
        Check(result.Output("outcome") == "failed-no-previous-release", "outcome must be failed-no-previous-release", result.Log);
        Check(!s.Called("helm rollback"), "a first install has nothing to roll back to");
        Check(result.Output("failed-resources") == "deployment/web,pod/web-new", "failed resources must name the Deployment and the pod", result.Output("failed-resources"));
        var before = Path.Combine(s.ReportDir, "diagnostics", "before-rollback");
        foreach (var file in new[] { "helm-status.txt", "helm-history.txt", "workloads.txt", "events.txt", "describe-web-new.txt",
                     Path.Combine("logs", "web-new__migrator.log"), Path.Combine("logs", "web-new__web.log"), Path.Combine("logs", "web-new__web.previous.log") })
        {
            Check(File.Exists(Path.Combine(before, file)), $"diagnostics must include {file}");
        }

        Check(File.ReadAllText(Path.Combine(before, "describe-web-new.txt")).Contains("ConnectionStrings__Db:  <redacted>", StringComparison.Ordinal),
            "describe output must keep variable names and redact their values");
        Check(File.ReadAllText(Path.Combine(before, "describe-web-new.txt")).Contains("<set to the key 'token' in secret 'bot'>", StringComparison.Ordinal),
            "describe output must keep secret references, which name a source and not a value");
        foreach (var file in Directory.EnumerateFiles(s.ReportDir, "*", SearchOption.AllDirectories))
        {
            Check(!File.ReadAllText(file).Contains(SecretLiteral, StringComparison.Ordinal), $"environment values must never be stored: {file}");
        }

        Check(!result.Log.Contains(SecretLiteral, StringComparison.Ordinal), "environment values must never be printed");
        Check(!s.Called("get secret") && !s.Called("helm get"), "the guard must never read Secrets, release values, or manifests");
    }

    // ---- Upgrade fails, rollback and schema hook succeed. Order matters: diagnostics, render, rollback, hook, verify.
    {
        var s = NewScenario("upgrade-failure-rolled-back");
        s.History((2, "superseded"), (3, "deployed"));
        s.CronJob(generation: 4);
        var before = DateTime.UtcNow.AddSeconds(-1);
        var capture = s.Guard("capture");
        Check(capture.ExitCode == 0, "capture must succeed", capture.Log);
        var startedAt = capture.Output("deploy-started-at");
        Check(System.Text.RegularExpressions.Regex.IsMatch(startedAt, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"),
            "the deployment start must be an RFC 3339 UTC timestamp with second precision and a Z suffix", startedAt);
        var startedAtInstant = DateTime.ParseExact(startedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        Check(startedAtInstant >= before.AddSeconds(-1) && startedAtInstant <= DateTime.UtcNow, "the deployment start must be taken at capture time, before the deploy", startedAt);
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "release-state.json")))!["deploy_started_at"]!.GetValue<string>() == startedAt,
            "the deployment start must be persisted in the state file");

        s.History((2, "superseded"), (3, "deployed"), (4, "failed"));
        s.HistoryAfterRollback((2, "superseded"), (3, "superseded"), (4, "failed"), (5, "deployed"));
        s.CronJob(generation: 5);
        s.BreakDeployment("web", healedByRollback: true);
        s.Write("apply-method", "ssa");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0, "the job must end red even though the rollback succeeded");
        Check(result.Output("outcome") == "rolled-back", "outcome must be rolled-back", result.Log);
        Check(result.Output("previous-revision") == "3" && result.Output("new-revision") == "4" && result.Output("current-revision") == "5",
            "rollback must report previous, failed, and live revision");
        Check(result.Output("schema-rollback") == "succeeded", "the schema hook must have run", result.Log);
        Check(result.Summary.Contains("**DEPLOYMENT FAILED - rolled back to revision 3, the previous release is live.**", StringComparison.Ordinal),
            "the summary must say the environment was restored", result.Summary);
        Check(!result.Summary.Contains("The deployment failed and", StringComparison.Ordinal), "the summary must not repeat its headline", result.Summary);
        Check(result.Summary.Contains("**Why it failed**\n\n    Upgrade \"fixture\" failed: conflict occurred while applying object", StringComparison.Ordinal)
            && result.Summary.Contains("\n    - .spec.replicas\n", StringComparison.Ordinal),
            "the summary must carry Helm's exact error, every line of it", result.Summary);
        Check(result.Summary.Contains("| Revision before this deployment | `3` |", StringComparison.Ordinal)
            && result.Summary.Contains("| Revision this deployment created | `4 (failed)` |", StringComparison.Ordinal)
            && result.Summary.Contains("| Revision live now | `5 (rolled back from 4 to 3: same content as revision 3)` |", StringComparison.Ordinal),
            "the revision rows must say what each revision is in plain words", result.Summary);
        Check(!result.Summary.Contains("(deployed)", StringComparison.Ordinal), "a bare Helm status must not appear next to a revision", result.Summary);
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!["failure_reason"]!.GetValue<string>().EndsWith("- .spec.replicas", StringComparison.Ordinal),
            "the report must carry Helm's error");
        Check(result.Log.Contains("::error::Cause: Upgrade \"fixture\" failed: conflict occurred", StringComparison.Ordinal), "the cause must be annotated on the run", result.Log);
        Check(s.Called($"helm rollback {Release} 3 --namespace example-production --wait --timeout 15m --force-conflicts"),
            "a server-side release must be rolled back over manual edits", s.Calls);
        Check(!result.Summary.Contains("ROLLBACK FAILED", StringComparison.Ordinal), "a clean rollback must not read as a failed one");

        var logs = s.IndexOfCall("kubectl logs web-new");
        var render = s.IndexOfCall("kubectl create job schema-rollback-4242-1 --from=cronjob/schema-rollback");
        var rollback = s.IndexOfCall($"helm rollback {Release} 3 --namespace example-production --wait --timeout 15m");
        var apply = s.IndexOfCall("kubectl create --filename");
        var verify = s.LastIndexOfCall("kubectl rollout status deployment/web");
        Check(logs >= 0 && render >= 0 && rollback >= 0 && apply >= 0 && verify >= 0, "every rollback stage must run", s.Calls);
        Check(logs < rollback, "diagnostics must be collected before the rollback removes the failing pods");
        Check(render < rollback, "the schema Job must be rendered before the rollback reverts its CronJob");
        Check(rollback < apply, "the schema Job must be applied only after the rollback, when no new pod uses the schema");
        Check(apply < verify, "rollouts must be verified after the rollback");

        var job = JsonNode.Parse(File.ReadAllText(Path.Combine(s.FakeDir, "created-job.json")))!;
        Check(job["metadata"]!["ownerReferences"] is null, "the Job must not be owned by the CronJob the rollback reverts or deletes");
        Check(job["spec"]!["ttlSecondsAfterFinished"]!.GetValue<int>() == 86400, "the Job must carry a ttl backstop");
        Check(job["metadata"]!["labels"]!["arkanis.dev/deploy-guard"]!.GetValue<string>() == "schema-rollback", "the Job must be labelled");
        var podSpec = job["spec"]!["template"]!["spec"]!;
        var jobContainers = podSpec["initContainers"]!.AsArray().Concat(podSpec["containers"]!.AsArray()).ToList();
        Check(jobContainers.Count == 3, "the fixture Job has one init container and two containers");
        foreach (var container in jobContainers)
        {
            var entries = container!["env"]!.AsArray().Where(entry => entry!["name"]!.GetValue<string>() == "ARKANIS_DEPLOY_STARTED_AT").ToList();
            Check(entries.Count == 1, $"container {container["name"]} must carry ARKANIS_DEPLOY_STARTED_AT exactly once, replacing a chart-provided entry");
            Check(entries[0]!["value"]?.GetValue<string>() == startedAt && entries[0]!["valueFrom"] is null,
                $"container {container["name"]} must receive the captured deployment start", entries[0]!.ToJsonString());
        }

        Check(podSpec["containers"]![0]!["env"]!.AsArray().Any(entry => entry!["name"]!.GetValue<string>() == "X"), "the chart's other variables must be kept");
        Check(s.Called("kubectl delete job schema-rollback-4242-1"), "a completed schema Job must be deleted");
        Check(!File.Exists(Path.Combine(s.RunnerTemp, "deploy-guard", "schema-rollback-job.json")), "the rendered pod spec must not be left behind");
        Check(Directory.EnumerateFiles(s.ReportDir, "*", SearchOption.AllDirectories).All(file => !File.ReadAllText(file).Contains("schema-env-literal", StringComparison.Ordinal)),
            "the rendered Job pod spec must not reach the uploaded report");
        Check(File.Exists(Path.Combine(s.ReportDir, "diagnostics", "schema-rollback", "job-logs.txt")), "schema Job logs must be captured");
        Check(result.Summary.Contains("**What the schema rollback Job reported** (last lines of its log)\n\n    sql noise 2\n    sql noise 3\n    warn: Fixture.Migrator[1]\n          Rolled back release 2: reverted 7 migration(s).\n    info: Fixture.Migrator[5]\n          Release schema rollback completed: RolledBack.\n", StringComparison.Ordinal),
            "the summary must repeat the closing lines of the schema Job's log, without kubectl's timestamps", result.Summary);
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!["schema_rollback_output"]!.GetValue<string>().EndsWith("Release schema rollback completed: RolledBack.", StringComparison.Ordinal),
            "the report must carry what the schema Job said it did");
        Check(result.Log.Contains("Rolled back release 2: reverted 7 migration(s).", StringComparison.Ordinal), "the schema Job's log must be echoed into the run log");
        Check(File.Exists(Path.Combine(s.ReportDir, "diagnostics", "after-rollback", "helm-history.txt")), "post-rollback state must be captured");
        Check(!s.Called("kubectl scale"), "a rolling rollback must not scale anything", s.Calls);
    }

    // ---- Success, but the workloads cannot be listed: still green (Helm waited), with the cause named and no false claim.
    {
        var s = NewScenario("success-workloads-unlistable");
        s.History((3, "deployed"));
        s.Guard("capture");
        s.History((3, "superseded"), (4, "deployed"));
        File.Delete(Path.Combine(s.FakeDir, "workloads.json"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed", "an unlistable namespace must not fail a deployment Helm already waited for", result.Log);
        Check(result.Log.Contains("Could not list the workloads in the namespace (kubectl exited with code", StringComparison.Ordinal)
            && result.Log.Contains("workloads.json", StringComparison.Ordinal), "the warning must carry kubectl's exit code and standard error", result.Log);
        Check(!result.Summary.Contains("every Deployment of the release is rolled out", StringComparison.Ordinal)
            && result.Summary.Contains("The rollout was not verified independently: the workloads in the namespace could not be listed.", StringComparison.Ordinal),
            "the summary must not claim a verification that never ran", result.Summary);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!;
        Check(!report["rollout_verified"]!.GetValue<bool>(), "the report must record that the rollout was not verified");
    }

    // ---- No kubectl call may carry --request-timeout: it disables in-cluster credentials on runner pods.
    {
        var source = File.ReadAllText(guardScript);
        var offenders = source.Split('\n').Where(line => line.Contains("Run(\"kubectl\"", StringComparison.Ordinal) && line.Contains("--request-timeout", StringComparison.Ordinal)).ToList();
        Check(offenders.Count == 0, "kubectl must be bounded by the process deadline only, never by --request-timeout", string.Join('\n', offenders));
    }

    // ---- Exclusive success: the release is stopped before the deploy, Helm restores the replicas, downtime reported.
    {
        var s = NewScenario("exclusive-success");
        s.History((3, "deployed"));
        s.RunningPods();
        var capture = s.Guard("capture", exclusive: true);
        Check(capture.ExitCode == 0, "an exclusive capture must succeed when the release stops", capture.Log);
        Check(capture.Output("exclusive") == "true", "the deploy step must learn that the release is stopped", capture.Log);
        Check(s.Called("kubectl scale deployment/web --namespace example-production --replicas=0"), "every Deployment of the release must be scaled to 0", s.Calls);
        Check(!s.Called("deployment/unrelated"), "a Deployment of another release must not be touched", s.Calls);
        Check(!s.Called("statefulset/"), "StatefulSets stay up: the migrator needs the data tier", s.Calls);
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "release-state.json")))!["exclusive"]!;
        Check(state["status"]!.GetValue<string>() == "stopped" && state["workloads"]![0]!["name"]!.GetValue<string>() == "web"
            && state["workloads"]![0]!["replicas"]!.GetValue<long>() == 2 && state["downtime_started_at"]!.GetValue<string>().Length > 0,
            "the pre-scale replica counts and the start of the downtime must be in the state file", state.ToJsonString());

        s.HelmApplied();
        s.History((3, "superseded"), (4, "deployed"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed", "a verified exclusive deployment must end green", result.Log);
        Check(!s.Called("--replicas=2"), "replicas Helm restored itself must not be scaled again", s.Calls);
        Check(s.LastIndexOfCall("kubectl rollout status deployment/web") > s.IndexOfCall("--replicas=0"), "rollouts must be verified after the deploy");
        Check(result.Output("downtime-started-at").Length > 0 && result.Output("downtime-ended-at").Length > 0, "the downtime window must be an output", result.Log);
        Check(result.Summary.Contains($"This deployment was exclusive: the service was down from {result.Output("downtime-started-at")} to {result.Output("downtime-ended-at")}.", StringComparison.Ordinal),
            "the summary must state the downtime window", result.Summary);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!["exclusive"]!;
        Check(report["workloads"]![0]!["replicas"]!.GetValue<long>() == 2 && report["restore"]!.GetValue<string>() == "restored",
            "the report must carry the stopped replica counts", report.ToJsonString());
    }

    // ---- Exclusive success whose chart leaves replicas alone: a Deployment still at 0 is put back.
    {
        var s = NewScenario("exclusive-success-left-at-zero");
        s.History((3, "deployed"));
        Check(s.Guard("capture", exclusive: true).ExitCode == 0, "capture must succeed");
        s.History((3, "superseded"), (4, "deployed"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed", "the deployment must end green once the replicas are back", result.Log);
        Check(s.Called("kubectl scale deployment/web --namespace example-production --replicas=2"), "a Deployment left at 0 must be scaled back", s.Calls);
        Check(result.Summary.Contains("| Replicas restored by the guard | `web` |", StringComparison.Ordinal), "the summary must say the guard restored it", result.Summary);
    }

    // ---- Exclusive deploy fails: stop the failed release, revert the schema, roll back, restore the replicas, verify.
    {
        var s = NewScenario("exclusive-failure-rolled-back");
        s.History((3, "deployed"));
        s.CronJob(generation: 1, uid: "uid-release-3");
        s.RunningPods();
        Check(s.Guard("capture", exclusive: true).ExitCode == 0, "capture must succeed");

        s.HelmApplied();
        s.History((3, "deployed"), (4, "failed"));
        s.HistoryAfterRollback((3, "superseded"), (4, "failed"), (5, "deployed"));
        s.CronJob(generation: 1, uid: "uid-release-4");
        s.BreakDeployment("web", healedByRollback: true);
        s.Write("rollback-keeps-scale", "1");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0 && result.Output("outcome") == "rolled-back", "a failed exclusive deployment must be rolled back and end red", result.Log);
        Check(result.Output("schema-rollback") == "succeeded", "the schema hook must run", result.Log);

        var logs = s.IndexOfCall("kubectl logs web-new");
        var render = s.IndexOfCall("kubectl create job schema-rollback-4242-1");
        var stopFailed = render < 0 ? -1 : s.Calls.IndexOf("kubectl scale deployment/web --namespace example-production --replicas=0", render, StringComparison.Ordinal);
        var schema = s.IndexOfCall("kubectl create --filename");
        var rollback = s.IndexOfCall($"helm rollback {Release} 3 --namespace example-production --wait --timeout 15m");
        var restore = s.IndexOfCall("kubectl scale deployment/web --namespace example-production --replicas=2");
        var verify = s.LastIndexOfCall("kubectl rollout status deployment/web");
        Check(logs >= 0 && render >= 0 && stopFailed >= 0 && schema >= 0 && rollback >= 0 && restore >= 0 && verify >= 0, "every exclusive rollback stage must run", s.Calls);
        Check(logs < render && render < stopFailed && stopFailed < schema && schema < rollback && rollback < restore && restore < verify,
            "order: diagnostics, render, stop the failed release, schema Job, helm rollback, restore replicas, verify", s.Calls);
        Check(result.Summary.Contains("This deployment was exclusive: the service was down from ", StringComparison.Ordinal), "the summary must state the downtime window", result.Summary);
        Check(result.Output("downtime-ended-at").Length > 0, "a restored release ends the downtime", result.Log);
    }

    // ---- Exclusive: the Helm rollback restores the replicas itself; the guard checks and does not scale again.
    {
        var s = NewScenario("exclusive-failure-helm-restores");
        s.History((3, "deployed"));
        Check(s.Guard("capture", exclusive: true).ExitCode == 0, "capture must succeed");
        s.HelmApplied();
        s.History((3, "deployed"), (4, "failed"));
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "failure");
        Check(result.Output("outcome") == "rolled-back" && result.Output("schema-rollback") == "not-found", "a rollback without a schema hook must still restore the release", result.Log);
        Check(s.Called("kubectl get deployment web --namespace example-production --ignore-not-found --output jsonpath={.spec.replicas}"), "the replica count must be checked, not assumed", s.Calls);
        Check(!s.Called("--replicas=2"), "replicas the rollback restored must not be scaled again", s.Calls);
    }

    // ---- Exclusive stop never completes (a terminating pod stays): no deploy, replicas restored, distinct outcome.
    {
        var s = NewScenario("exclusive-stop-times-out");
        s.History((3, "deployed"));
        s.RunningPods(terminating: true);
        s.Write("pods-stuck", "1");
        var capture = s.Guard("capture", timeout: "1s", exclusive: true);
        Check(capture.ExitCode != 0, "a stop that does not complete must stop the job before the deploy", capture.Log);
        Check(capture.Output("exclusive") == "false", "the deploy step must not be told it may migrate", capture.Log);
        Check(capture.Log.Contains("web-old", StringComparison.Ordinal), "the pods still present must be named", capture.Log);
        Check(s.IndexOfCall("--replicas=0") < s.IndexOfCall("kubectl scale deployment/web --namespace example-production --replicas=2"), "the recorded replicas must be restored after the failed stop", s.Calls);
        Check(s.Called("kubectl rollout status deployment/web"), "the restored release must be verified", s.Calls);
        var result = s.Guard("finalize", "skipped", timeout: "1s");
        Check(result.ExitCode != 0 && result.Output("outcome") == "failed-exclusive-stop", "a failed exclusive stop must have its own outcome", result.Log);
        Check(!s.Called("helm rollback") && !s.Called("helm upgrade"), "nothing may be deployed or rolled back", s.Calls);
        Check(result.Summary.Contains("This deployment was exclusive: the service was down from ", StringComparison.Ordinal), "the summary must state the downtime", result.Summary);
    }

    // ---- Exclusive stop: the scale call itself fails. Restore what may have changed, no deploy.
    {
        var s = NewScenario("exclusive-scale-fails");
        s.History((3, "deployed"));
        s.Write("scale-zero-exit", "1");
        Check(s.Guard("capture", exclusive: true).ExitCode != 0, "a failed scale-down must stop the job before the deploy");
        var result = s.Guard("finalize", "skipped");
        Check(result.Output("outcome") == "failed-exclusive-stop" && s.Called("kubectl get deployment web"), "the replica counts must be checked after a failed scale-down", result.Log);
    }

    // ---- Exclusive first install: nothing runs yet, so nothing is stopped and the deploy may migrate.
    {
        var s = NewScenario("exclusive-first-install");
        var capture = s.Guard("capture", exclusive: true);
        Check(capture.ExitCode == 0 && capture.Output("exclusive") == "true", "an exclusive first install must proceed", capture.Log);
        Check(!s.Called("kubectl scale") && !s.Called("kubectl get deployments"), "a first install has nothing to stop", s.Calls);
        s.History((1, "deployed"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed", "an exclusive first install must deploy", result.Log);
        Check(result.Summary.Contains("first install, so nothing was stopped", StringComparison.Ordinal), "the summary must say nothing was stopped", result.Summary);
    }

    // ---- Exclusive deploy fails before Helm touched the release: put the stopped release back as it was.
    {
        var s = NewScenario("exclusive-failed-before-helm");
        s.History((3, "deployed"));
        Check(s.Guard("capture", exclusive: true).ExitCode == 0, "capture must succeed");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0 && result.Output("outcome") == "failed-release-unchanged", "an untouched release is still unchanged", result.Log);
        Check(s.Called("kubectl scale deployment/web --namespace example-production --replicas=2") && !s.Called("helm rollback"),
            "the stopped release must be restored without a rollback", s.Calls);
    }

    // ---- Upgrade fails and the rollback fails too: loud, and the schema hook must not run.
    {
        var s = NewScenario("rollback-fails");
        s.History((3, "deployed"));
        s.CronJob(generation: 4);
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");

        s.History((3, "deployed"), (4, "failed"));
        s.CronJob(generation: 5);
        s.BreakDeployment("web");
        s.Write("rollback-exit", "1");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0, "a failed rollback must end red");
        Check(result.Output("outcome") == "rollback-failed", "outcome must be rollback-failed", result.Log);
        Check(result.Summary.Contains("ROLLBACK FAILED, environment needs attention", StringComparison.Ordinal), "the summary must flag the failed rollback");
        Check(!result.Summary.Contains("environment restored", StringComparison.Ordinal), "a failed rollback must not read as restored");
        Check(!s.Called("kubectl create --filename"), "the schema hook must not run while new-version pods may still use the schema");
        Check(result.Output("schema-rollback") == "skipped", "the skipped schema hook must be reported");
    }

    // ---- Rollback succeeds but the workloads never become ready.
    {
        var s = NewScenario("rollback-not-ready");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.BreakDeployment("web");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0 && result.Output("outcome") == "rollback-failed", "workloads still broken after the rollback mean rollback-failed", result.Log);
    }

    // ---- Stuck release from a cancelled run is recovered before deploying.
    {
        var s = NewScenario("stuck-pending-upgrade");
        s.History((3, "deployed"), (4, "pending-upgrade"));
        s.HistoryAfterRollback((3, "superseded"), (4, "pending-upgrade"), (5, "deployed"));
        var capture = s.Guard("capture");
        Check(capture.ExitCode == 0, "a stuck release with a deployed revision must be recovered", capture.Log);
        Check(s.Called($"helm rollback {Release} 3 --namespace example-production --wait --timeout 15m"), "recovery must roll back to the last deployed revision");
        Check(capture.Output("recovered") == "true" && capture.Output("previous-revision") == "5", "capture must report the recovery and the recovered revision");

        // The deploy then fails: the rollback target is the recovered revision, not the pending one.
        s.History((3, "superseded"), (4, "pending-upgrade"), (5, "deployed"), (6, "failed"));
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "failure");
        Check(result.Output("outcome") == "rolled-back" && s.Called($"helm rollback {Release} 5 "), "a later failure must return to the recovered revision", result.Log);
    }

    // ---- Stuck first install: nothing to return to, refuse to deploy instead of guessing.
    {
        var s = NewScenario("stuck-pending-install");
        s.History((1, "pending-install"));
        var capture = s.Guard("capture");
        Check(capture.ExitCode != 0, "a stuck first install must stop the job before deploying");
        Check(!s.Called("helm rollback") && !s.Called("helm uninstall"), "the guard must not guess a destructive recovery");
        var result = s.Guard("finalize", "skipped");
        Check(result.ExitCode != 0 && result.Output("outcome") == "rollback-failed", "an unrecoverable stuck release must be reported as needing attention", result.Log);
        Check(result.Summary.Contains("**DEPLOYMENT NOT STARTED - the Helm release was already stuck", StringComparison.Ordinal) && !result.Summary.Contains("ROLLBACK FAILED", StringComparison.Ordinal),
            "a release stuck before the deployment must not be reported as a failed rollback", result.Summary);
    }

    // ---- Failed recovery stops the job before deploying.
    {
        var s = NewScenario("stuck-recovery-fails");
        s.History((3, "deployed"), (4, "pending-rollback"));
        s.Write("rollback-exit", "1");
        Check(s.Guard("capture").ExitCode != 0, "a failed recovery must stop the job before deploying");
        var result = s.Guard("finalize", "skipped");
        Check(result.ExitCode != 0 && result.Output("outcome") == "rollback-failed", "a failed recovery must be reported as needing attention", result.Log);
    }

    // ---- Unreadable release: fail closed before deploying.
    {
        var s = NewScenario("unreadable");
        s.Write("helm-list-exit", "1");
        var capture = s.Guard("capture");
        Check(capture.ExitCode != 0, "capture must fail closed when the release cannot be read");
        var result = s.Guard("finalize", "skipped");
        Check(result.Output("outcome") == "not-attempted", "an unreadable release means the deployment did not start, not a failed rollback", result.Log);
        Check(result.Summary.Contains("**Deployment did not start - environment untouched.**", StringComparison.Ordinal)
            && !result.Summary.Contains("ROLLBACK FAILED", StringComparison.Ordinal) && !result.Log.Contains("ROLLBACK FAILED", StringComparison.Ordinal),
            "nothing was touched, so nothing may claim a failed rollback or an environment needing attention", result.Summary);
        Check(result.Summary.Contains("**Why it failed**\n\n    Error: Kubernetes cluster unreachable", StringComparison.Ordinal), "the summary must repeat why the release could not be read", result.Summary);
        Check(result.Summary.Contains("| Revision before this deployment | `unknown (the release could not be read)` |", StringComparison.Ordinal),
            "an unreadable release must not be reported as a first install", result.Summary);
        Check(!s.Called("helm rollback") && !s.Called("helm upgrade"), "an unreadable release must not be changed");
    }

    // ---- Schema CronJob absent: skipped silently, rollback still clean.
    {
        var s = NewScenario("schema-cronjob-absent");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "failure");
        Check(result.Output("outcome") == "rolled-back" && result.Output("schema-rollback") == "not-found", "a missing CronJob must not affect the rollback", result.Log);
        Check(!s.Called("kubectl create job") && !result.Log.Contains("::error::SCHEMA", StringComparison.Ordinal), "a missing CronJob must be skipped without an error");
        Check(result.Log.Contains("No CronJob 'schema-rollback'", StringComparison.Ordinal), "a missing CronJob must leave a log line");
    }

    // ---- Schema CronJob untouched by this deploy (same uid, same generation) still belongs to the previous release: never run it.
    {
        var s = NewScenario("schema-cronjob-unchanged");
        s.History((3, "deployed"));
        s.CronJob(generation: 4);
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "failure");
        Check(result.Output("outcome") == "rolled-back" && result.Output("schema-rollback") == "skipped", "an unchanged CronJob must be skipped", result.Log);
        Check(!s.Called("kubectl create job"), "running the previous release's hook would undo the schema being restored");
    }

    // ---- Helm hook shape (pre-install,pre-upgrade + before-hook-creation): Helm deletes and recreates the CronJob
    //      on every upgrade, so the uid changes while the generation is 1 both before and after. It must still run.
    {
        var s = NewScenario("schema-cronjob-helm-hook");
        s.History((3, "deployed"));
        s.CronJob(generation: 1, uid: "uid-release-3");
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.CronJob(generation: 1, uid: "uid-release-4");
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "failure");
        Check(result.Output("outcome") == "rolled-back" && result.Output("schema-rollback") == "succeeded",
            "a recreated hook CronJob belongs to the failed release and must run even though its generation did not change", result.Log);
        Check(s.IndexOfCall("kubectl create job schema-rollback-4242-1 --from=cronjob/schema-rollback") >= 0
            && s.IndexOfCall("helm rollback") < s.IndexOfCall("kubectl create --filename"), "the hook Job must be rendered and applied after the rollback", s.Calls);
    }

    // ---- Schema hook fails: workloads restored, but the run must say the database was not.
    {
        var s = NewScenario("schema-rollback-fails");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.CronJob(generation: 1);
        s.BreakDeployment("web", healedByRollback: true);
        s.Write("job-status.json", """{"status":{"conditions":[{"type":"Failed","status":"True"}]}}""");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0 && result.Output("outcome") == "rollback-failed" && result.Output("schema-rollback") == "failed",
            "a failed schema hook must not be reported as a clean rollback", result.Log);
        Check(result.Log.Contains("::error::SCHEMA ROLLBACK FAILED", StringComparison.Ordinal), "a failed schema hook must be loud");
        Check(result.Summary.Contains("workloads restored to revision 3, SCHEMA ROLLBACK FAILED", StringComparison.Ordinal),
            "the summary must distinguish a failed schema rollback from a failed Helm rollback");
        Check(s.Called("kubectl rollout status deployment/web"), "verification must still run after a failed schema hook");
        Check(!s.Called("kubectl delete job"), "a failed schema Job is evidence and must stay");
    }

    // ---- Schema hook never finishes: bounded wait, reported as timed out.
    {
        var s = NewScenario("schema-rollback-times-out");
        s.History((3, "deployed"));
        Check(s.Guard("capture", timeout: "1s").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.CronJob(generation: 1);
        s.BreakDeployment("web", healedByRollback: true);
        s.Write("job-status.json", """{"status":{}}""");
        var result = s.Guard("finalize", "failure", timeout: "1s");
        Check(result.Output("schema-rollback") == "timed-out" && result.Output("outcome") == "rollback-failed", "the schema Job wait must be bounded", result.Log);
    }

    // ---- Helm said success but a Deployment is not rolled out: the job cannot end green.
    {
        var s = NewScenario("helm-success-rollout-broken");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "superseded"), (4, "deployed"));
        s.BreakDeployment("web", healedByRollback: true);
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode != 0, "a deployment whose rollout verification fails must end red");
        Check(result.Output("outcome") == "rolled-back" && s.Called($"helm rollback {Release} 3 "), "a failed verification must roll back to the previous revision", result.Log);
    }

    // ---- Deploy failed before Helm touched the release (build, push, render): nothing to restore.
    {
        var s = NewScenario("failed-before-helm");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0 && result.Output("outcome") == "failed-release-unchanged", "an untouched release must be reported as unchanged", result.Log);
        Check(!s.Called("helm rollback") && !s.Called("kubectl logs"), "an untouched release needs neither rollback nor pod logs");
    }

    // ---- rollback-on-failure: false leaves the release alone but still fails and collects diagnostics.
    {
        var s = NewScenario("rollback-disabled");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.History((3, "deployed"), (4, "failed"));
        s.BreakDeployment("web");
        var result = s.Guard("finalize", "failure", rollbackOnFailure: "false");
        Check(result.ExitCode != 0 && result.Output("outcome") == "failed-rollback-disabled", "a disabled rollback must be reported", result.Log);
        Check(!s.Called("helm rollback") && s.Called("kubectl logs web-new"), "a disabled rollback still collects diagnostics and restores nothing");
    }

    // ---- Earlier step failed or the run was cancelled: no cluster mutation.
    {
        var s = NewScenario("skipped-and-cancelled");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        var skipped = s.Guard("finalize", "skipped");
        Check(skipped.ExitCode == 0 && skipped.Output("outcome") == "not-attempted", "a deployment that never started is not a guard failure", skipped.Log);
        var cancelled = s.Guard("finalize", "cancelled");
        Check(cancelled.ExitCode != 0 && cancelled.Output("outcome") == "cancelled", "a cancelled deployment must be reported", cancelled.Log);
        Check(s.Calls.Length == 0, "skipped and cancelled runs must not touch the cluster", s.Calls);
    }

    // ---- The AppHost names the release differently: the shim's record wins.
    {
        var s = NewScenario("release-name-adopted");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        File.WriteAllText(s.ShimRecord, "release=patchwatch\nnamespace=example-production\n");
        File.WriteAllText(s.ShimRecord + ".history.json", HistoryJson((7, "deployed")));
        s.History((7, "deployed"), (8, "failed"));
        s.BreakDeployment("web", healedByRollback: true, release: "patchwatch");
        var result = s.Guard("finalize", "failure");
        Check(result.Output("helm-release") == "patchwatch" && result.Output("outcome") == "rolled-back", "the release the deploy really targeted must be restored", result.Log);
        Check(s.Called("helm rollback patchwatch 7 "), "the rollback must target the observed release and its own previous revision");
        Check(result.Log.Contains("::warning::The AppHost deployed Helm release 'patchwatch'", StringComparison.Ordinal), "the mismatch must be flagged");
    }

    // ---- Input validation fails before anything is deployed.
    {
        var s = NewScenario("invalid-input");
        Check(s.Guard("capture", timeout: "soon").ExitCode == 2, "an invalid rollout-timeout must be rejected before deploying");
        Check(s.Guard("capture", release: "Not_A_Release").ExitCode == 2, "an invalid release name must be rejected before deploying");
        Check(s.Calls.Length == 0, "invalid input must not reach the cluster");
    }

    // ---- The shim: timeout on upgrade only, everything else untouched.
    {
        var s = NewScenario("shim");
        s.History((3, "deployed"));
        var realHelm = Path.Combine(s.BinDir, "helm");
        var upgrade = Shim(s, realHelm, "upgrade", "--install", Release, "/chart dir/with space", "--namespace", "example-production", "--wait", "-f", "values.yaml");
        Check(upgrade.ExitCode == 0, "the shim must pass the upgrade through", upgrade.Stderr);
        Check(s.Called($"helm upgrade --install {Release} /chart dir/with space --namespace example-production --wait -f values.yaml --timeout 20m"),
            "the shim must append --timeout to helm upgrade and keep every argument intact", s.Calls);
        Check(File.ReadAllText(s.ShimRecord) == $"release={Release}\nnamespace=example-production\n", "the shim must record the release and namespace");
        Check(JsonNode.Parse(File.ReadAllText(s.ShimRecord + ".history.json"))!.AsArray().Count == 1, "the shim must record the history before the upgrade");

        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production", "--timeout", "3m").ExitCode == 0, "explicit timeout passes");
        Check(s.Called("--timeout 3m") && !s.Called("--timeout 20m"), "the shim must not override a timeout Aspire sets itself", s.Calls);
        Check(!s.Called("--force-conflicts"), "the shim must not force conflicts on a release whose apply method is unknown", s.Calls);

        // Server-side release: the chart must win over a field someone changed by hand.
        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        s.Write("apply-method", "ssa");
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production", "--wait").ExitCode == 0, "server-side upgrade passes");
        Check(s.Called($"helm upgrade --install {Release} chart --namespace example-production --wait --force-conflicts --timeout 20m"),
            "the shim must append --force-conflicts for a server-side release", s.Calls);

        // Client-side release: Helm rejects the flag there and records a failed revision.
        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        s.Write("apply-method", "csa");
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production", "--wait").ExitCode == 0, "client-side upgrade passes");
        Check(!s.Called("--force-conflicts"), "the shim must not force conflicts on a client-side release", s.Calls);

        // An explicit apply option from the caller is left alone.
        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        s.Write("apply-method", "ssa");
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production", "--server-side=false").ExitCode == 0, "explicit apply option passes");
        Check(!s.Called("--force-conflicts"), "the shim must not add --force-conflicts next to an explicit apply option", s.Calls);
        File.Delete(Path.Combine(s.FakeDir, "apply-method"));

        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        Check(Shim(s, realHelm, "version", "--short").ExitCode == 0 && Shim(s, realHelm, "rollback", Release, "3", "--namespace", "example-production").ExitCode == 0,
            "other subcommands pass");
        Check(s.Calls == $"helm version --short\nhelm rollback {Release} 3 --namespace example-production\n", "other subcommands must pass through untouched", s.Calls);

        s.Write("upgrade-exit", "1");
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production").ExitCode == 1, "the shim must return Helm's exit code");
    }

    // ---- Schema confirm disabled (empty input): not even the CronJob is read.
    {
        var s = ConfirmScenario("schema-confirm-not-configured");
        var result = s.Guard("finalize", "success", confirmCronJob: "");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "not-configured",
            "an empty schema-confirm-cronjob must disable the confirm", result.Log);
        Check(!s.Called("get cronjob schema-confirm") && !s.Called("replicasets,cronjobs,jobs,pods"), "a disabled confirm must not touch the cluster", s.Calls);
    }

    // ---- Schema confirm on a clean namespace: the Job runs after the rollout is verified, without the deploy start.
    {
        var s = ConfirmScenario("schema-confirm-succeeds");
        var startedAt = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "release-state.json")))!["deploy_started_at"]!.GetValue<string>();
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "succeeded",
            "a clean namespace must be confirmed and the job must stay green", result.Log);
        var verify = s.LastIndexOfCall("kubectl rollout status deployment/web");
        var inspect = s.IndexOfCall("kubectl get deployments,statefulsets,daemonsets,replicasets,cronjobs,jobs,pods --namespace example-production --output json");
        var render = s.IndexOfCall("kubectl create job schema-confirm-4242-1 --from=cronjob/schema-confirm --namespace example-production --dry-run=client");
        var create = s.IndexOfCall("kubectl create --filename");
        Check(verify >= 0 && verify < inspect && inspect < render && render < create, "order: verify the rollout, look for traces, render, create", s.Calls);
        Check(!s.Called("helm rollback") && s.Called("kubectl delete job schema-confirm-4242-1"), "a completed confirm Job is deleted and nothing is rolled back", s.Calls);
        Check(!result.Log.Contains("::warning::", StringComparison.Ordinal), "zero-replica ReplicaSets, finished pods and Jobs, and a shared third-party image at two tags are not traces", result.Log);

        var job = JsonNode.Parse(File.ReadAllText(Path.Combine(s.FakeDir, "created-job.json")))!;
        Check(job["metadata"]!["labels"]!["arkanis.dev/deploy-guard"]!.GetValue<string>() == "schema-confirm", "the confirm Job must be labelled");
        Check(job["spec"]!["ttlSecondsAfterFinished"]!.GetValue<int>() == 86400, "the confirm Job must carry a ttl backstop");
        Check(job["metadata"]!["ownerReferences"] is not null, "the confirm Job keeps the owner reference kubectl gives it");
        Check(!job.ToJsonString().Contains(startedAt, StringComparison.Ordinal)
            && job.ToJsonString().Contains("1999-01-01T00:00:00Z", StringComparison.Ordinal),
            "the confirm Job must not receive ARKANIS_DEPLOY_STARTED_AT, and the chart's own env stays untouched", job.ToJsonString());
        Check(!File.Exists(Path.Combine(s.RunnerTemp, "deploy-guard", "schema-confirm-job.json")), "the rendered pod spec must not be left behind");
        Check(File.Exists(Path.Combine(s.ReportDir, "diagnostics", "schema-confirm", "job-logs.txt")), "the confirm Job's logs must be captured");
        Check(result.Summary.Contains("**What the schema confirm Job reported** (last lines of its log)\n\n    checking release\n    info: Fixture.Migrator[7]\n          Release 4 rollout confirmed.\n", StringComparison.Ordinal)
            && result.Summary.Contains("| Schema confirm | `succeeded` |", StringComparison.Ordinal),
            "the summary must carry the confirm result and its Job's closing log lines", result.Summary);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!;
        Check(report["schema_confirm"]!.GetValue<string>() == "succeeded" && report["schema_confirm_job"]!.GetValue<string>() == "schema-confirm-4242-1"
            && report["schema_confirm_output"]!.GetValue<string>().EndsWith("Release 4 rollout confirmed.", StringComparison.Ordinal),
            "the report must carry the confirm result", report.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(s.ReportDir, "*", SearchOption.AllDirectories))
        {
            Check(!File.ReadAllText(file).Contains(SecretLiteral, StringComparison.Ordinal), $"the inventory's environment values must never be stored: {file}");
        }

        Check(!result.Log.Contains(SecretLiteral, StringComparison.Ordinal), "the inventory's environment values must never be printed");
    }

    // ---- Each kind of trace of an older release blocks the confirm: warning, report section, green job, no Job.
    foreach (var (name, items, culprit) in new (string, string[], string)[]
             {
                 ("old-deployment-at-zero", [Scenario.Item("Deployment", "web-stray", "ghcr.io/example/web:1", spec: "\"replicas\":0")], "Deployment/web-stray"),
                 ("paused-release-deployment", [Scenario.Item("Deployment", "web", "ghcr.io/example/web:2", owned: true, spec: "\"replicas\":2,\"paused\":true")], "Deployment/web"),
                 ("old-replicaset-with-replicas", [Scenario.Item("ReplicaSet", "web-1-live", "ghcr.io/example/web:1", spec: "\"replicas\":0", status: "{\"replicas\":1}")], "ReplicaSet/web-1-live"),
                 ("running-old-pod", [Scenario.Item("Pod", "web-1-a", "ghcr.io/example/web:1", status: "{\"phase\":\"Running\"}")], "Pod/web-1-a"),
                 ("old-init-container-pod", [Scenario.Item("Pod", "web-mixed", "ghcr.io/example/web:2", status: "{\"phase\":\"Pending\"}", initImage: "ghcr.io/example/migrator:1")], "Pod/web-mixed"),
                 ("suspended-stray-cronjob", [Scenario.Item("CronJob", "schema-confirm-v1", "ghcr.io/example/migrator:1", spec: "\"suspend\":true")], "CronJob/schema-confirm-v1"),
                 ("unfinished-old-job", [Scenario.Item("Job", "schema-rollback-old", "ghcr.io/example/migrator:1", spec: "\"suspend\":true")], "Job/schema-rollback-old"),
             })
    {
        var s = ConfirmScenario($"schema-confirm-blocked-{name}");
        s.Inventory([.. Scenario.CleanInventory().Where(item => name != "paused-release-deployment" || !item.Contains("\"name\":\"web\"", StringComparison.Ordinal)), .. items]);
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "blocked",
            $"{name}: a trace of an older release must block the confirm and keep the job green", result.Log);
        Check(!s.Called("kubectl create job schema-confirm") && !s.Called("kubectl create --filename") && !s.Called("helm rollback"),
            $"{name}: a blocked confirm must create no Job and roll nothing back", s.Calls);
        Check(result.Log.Contains($"::warning::Schema confirm blocked by {culprit}", StringComparison.Ordinal), $"{name}: the warning must name the object", result.Log);
        Check(result.Summary.Contains("**Schema confirm blocked**", StringComparison.Ordinal) && result.Summary.Contains($"- `{culprit}`", StringComparison.Ordinal),
            $"{name}: the summary must list the object", result.Summary);
        var traces = JsonNode.Parse(File.ReadAllText(Path.Combine(s.ReportDir, "deploy-report.json")))!["schema_confirm_traces"]!.AsArray();
        Check(traces.Count == 1 && $"{traces[0]!["kind"]}/{traces[0]!["name"]}" == culprit, $"{name}: the report must carry exactly that trace", traces.ToJsonString());
    }

    // ---- The namespace cannot be inspected (RBAC forbids a kind): blocked, never confirmed on missing evidence.
    {
        var s = ConfirmScenario("schema-confirm-inventory-forbidden");
        s.Write("inventory-exit", "1");
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "blocked", "an unreadable namespace must block the confirm", result.Log);
        Check(result.Log.Contains("::warning::SCHEMA CONFIRM BLOCKED: The namespace could not be inspected for an older release (kubectl exited with code 1; standard error: Error from server (Forbidden)", StringComparison.Ordinal),
            "the warning must carry kubectl's error", result.Log);
        Check(!s.Called("kubectl create --filename"), "no Job may run on missing evidence", s.Calls);
    }

    // ---- The confirm CronJob itself cannot be read: blocked, not mistaken for an absent CronJob.
    {
        var s = ConfirmScenario("schema-confirm-cronjob-forbidden");
        s.Write("confirm-cronjob-exit", "1");
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("schema-confirm") == "blocked", "a failed CronJob read must block, not skip", result.Log);
        Check(!s.Called("replicasets,cronjobs,jobs,pods") && !s.Called("kubectl create --filename"), "nothing else may run after a failed CronJob read", s.Calls);
    }

    // ---- The guard could not verify the rollout itself: blocked even though the deployment stays green.
    {
        var s = ConfirmScenario("schema-confirm-rollout-unverified");
        File.Delete(Path.Combine(s.FakeDir, "workloads.json"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "blocked", "an unverified rollout must block the confirm", result.Log);
        Check(!s.Called("kubectl create --filename"), "no Job may run without a verified rollout", s.Calls);
    }

    // ---- The confirm Job fails: the job goes red, the outcome stays deployed, nothing is rolled back.
    {
        var s = ConfirmScenario("schema-confirm-fails");
        s.Write("confirm-job-status.json", """{"status":{"conditions":[{"type":"Failed","status":"True"}]}}""");
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode != 0, "a failed confirm must turn the job red so it is noticed", result.Log);
        Check(result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "failed" && result.Output("current-revision") == "4",
            "a failed confirm must leave the outcome deployed", result.Log);
        Check(!s.Called("helm rollback") && !s.Called("kubectl delete job"), "a failed confirm must roll nothing back and keep its Job as evidence", s.Calls);
        Check(result.Log.Contains("::error::SCHEMA CONFIRM FAILED: Job schema-confirm-4242-1 failed", StringComparison.Ordinal), "a failed confirm must be an error annotation", result.Log);
        Check(result.Summary.Contains("**Deployed revision 4, but the SCHEMA CONFIRM FAILED", StringComparison.Ordinal), "the headline must say both", result.Summary);
        Check(File.Exists(Path.Combine(s.ReportDir, "deploy-report.json")) && File.Exists(Path.Combine(s.ReportDir, "diagnostics", "schema-confirm", "job-logs.txt")),
            "the report and the Job's logs must still be written", s.ReportDir);
    }

    // ---- The confirm Job never finishes: bounded wait, timed-out, red, still deployed.
    {
        var s = ConfirmScenario("schema-confirm-times-out", timeout: "1s");
        s.Write("confirm-job-status.json", """{"status":{}}""");
        var result = s.Guard("finalize", "success", timeout: "1s");
        Check(result.ExitCode != 0 && result.Output("outcome") == "deployed" && result.Output("schema-confirm") == "timed-out", "the confirm Job wait must be bounded", result.Log);
        Check(!s.Called("helm rollback") && !s.Called("kubectl delete job"), "a still-running confirm Job must not be killed or rolled back", s.Calls);
    }

    // ---- Never after anything but a verified deployment: rollback, failure before Helm, cancel, skip.
    foreach (var (name, outcome, expected) in new[]
             {
                 ("rolled-back", "failure", "rolled-back"),
                 ("verification-failed", "success", "rolled-back"),
                 ("release-unchanged", "failure", "failed-release-unchanged"),
                 ("cancelled", "cancelled", "cancelled"),
                 ("skipped", "skipped", "not-attempted"),
             })
    {
        var s = NewScenario($"schema-confirm-never-{name}");
        s.History((3, "deployed"));
        Check(s.Guard("capture").ExitCode == 0, "capture must succeed");
        s.ConfirmCronJob();
        s.Inventory(Scenario.CleanInventory());
        if (name is "rolled-back" or "verification-failed")
        {
            s.History(name == "rolled-back" ? [(3, "deployed"), (4, "failed")] : [(3, "superseded"), (4, "deployed")]);
            s.BreakDeployment("web", healedByRollback: true);
        }

        var result = s.Guard("finalize", outcome);
        Check(result.Output("outcome") == expected && result.Output("schema-confirm") == "not-run", $"{name}: the confirm must not run", result.Log);
        Check(!s.Called("get cronjob schema-confirm") && !s.Called("replicasets,cronjobs,jobs,pods") && !s.Called("schema-confirm-4242"),
            $"{name}: the confirm must not even be looked up", s.Calls);
    }

    Console.WriteLine("Helm release guard behavior passed.");
    return 0;
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

Scenario NewScenario(string name)
{
    var root = Path.Combine(testRoot, name);
    var scenario = new Scenario(root, guardScript);
    Directory.CreateDirectory(scenario.FakeDir);
    Directory.CreateDirectory(scenario.BinDir);
    Directory.CreateDirectory(scenario.RunnerTemp);
    foreach (var tool in new[] { "helm", "kubectl" })
    {
        var path = Path.Combine(scenario.BinDir, tool);
        File.WriteAllText(path, FakeCli.Script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    scenario.Workloads(Release, healthy: true);
    return scenario;
}

// A release at revision 3 captured, then deployed as revision 4 with the confirm CronJob and a clean namespace.
Scenario ConfirmScenario(string name, string timeout = "15m")
{
    var scenario = NewScenario(name);
    scenario.History((3, "deployed"));
    Check(scenario.Guard("capture", timeout: timeout).ExitCode == 0, "capture must succeed");
    scenario.History((3, "superseded"), (4, "deployed"));
    scenario.ConfirmCronJob();
    scenario.Inventory(Scenario.CleanInventory());
    return scenario;
}

ProcessOutput Shim(Scenario scenario, string realHelm, params string[] arguments)
{
    var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(shimScript);
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    start.Environment["ARKANIS_REAL_HELM"] = realHelm;
    start.Environment["ARKANIS_HELM_UPGRADE_TIMEOUT"] = "20m";
    start.Environment["ARKANIS_HELM_SHIM_RECORD"] = scenario.ShimRecord;
    start.Environment["FAKE_K8S_DIR"] = scenario.FakeDir;
    return ProcessOutput.Run(start);
}

static string HistoryJson(params (int Revision, string Status)[] entries) =>
    "[" + string.Join(",", entries.Select(entry => $$"""{"revision":{{entry.Revision}},"updated":"2026-11-19T10:00:00Z","status":"{{entry.Status}}","chart":"app-1.0.0","app_version":"1.0.0","description":"fixture"}""")) + "]";

static void Check(bool condition, string message, string detail = "")
{
    if (!condition)
    {
        throw new InvalidOperationException($"{message}: {detail}");
    }
}

sealed class Scenario(string root, string guardScript)
{
    const string SecretLiteral = "Host=db;Password=fixture-not-a-real-secret";

    public string FakeDir { get; } = Path.Combine(root, "fake");

    public string BinDir { get; } = Path.Combine(root, "bin");

    public string RunnerTemp { get; } = Path.Combine(root, "runner-temp");

    public string ReportDir { get; } = Path.Combine(root, "workspace", "artifacts", "deploy-report");

    public string ShimRecord => Path.Combine(RunnerTemp, "upgrade-record");

    public string Calls => File.Exists(Path.Combine(FakeDir, "calls.log")) ? File.ReadAllText(Path.Combine(FakeDir, "calls.log")) : string.Empty;

    public bool Called(string fragment) => Calls.Contains(fragment, StringComparison.Ordinal);

    public int IndexOfCall(string fragment) => Calls.IndexOf(fragment, StringComparison.Ordinal);

    public int LastIndexOfCall(string fragment) => Calls.LastIndexOf(fragment, StringComparison.Ordinal);

    public void Write(string name, string content) => File.WriteAllText(Path.Combine(FakeDir, name), content);

    public void History(params (int Revision, string Status)[] entries) => Write("history.json", Json(entries));

    public void HistoryAfterRollback(params (int Revision, string Status)[] entries) => Write("history-after-rollback.json", Json(entries));

    // What a Helm upgrade does to a release the exclusive stop scaled to 0: it re-applies the chart's replica counts.
    public void HelmApplied()
    {
        foreach (var file in Directory.EnumerateFiles(FakeDir, "replicas-*"))
        {
            File.Delete(file);
        }
    }

    // The previous version's pod, serving until the exclusive stop scales it away. A terminating pod carries a deletionTimestamp.
    public void RunningPods(bool terminating = false) => Write("pods.json", """
        {"items":[
          {"metadata":{"name":"web-old","labels":{"app":"web"}@DELETION@},"spec":{"containers":[{"name":"web"}]},
           "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}},
          {"metadata":{"name":"unrelated-1","labels":{"app":"unrelated"}},"spec":{"containers":[{"name":"main"}]},
           "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}}
        ]}
        """.Replace("@DELETION@", terminating ? ",\"deletionTimestamp\":\"2026-11-20T10:00:00Z\"" : string.Empty, StringComparison.Ordinal));

    // uid + generation are what the guard compares: a chart resource keeps its uid and bumps the generation,
    // a Helm hook with before-hook-creation is recreated (new uid) and starts at generation 1 again.
    public void CronJob(int generation, string uid = "uid-a") =>
        Write("cronjob.json", """{"kind":"CronJob","metadata":{"name":"schema-rollback","uid":"@UID@","generation":@GENERATION@},"spec":{"suspend":true}}"""
            .Replace("@UID@", uid, StringComparison.Ordinal)
            .Replace("@GENERATION@", generation.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

    public void ConfirmCronJob() =>
        Write("confirm-cronjob.json", """{"kind":"CronJob","metadata":{"name":"schema-confirm","uid":"uid-confirm","generation":1},"spec":{"suspend":true}}""");

    // What the schema confirm's trace check sees: one `kubectl get deployments,...,pods -o json` list.
    public void Inventory(params string[] items) => Write("inventory.json", "{\"kind\":\"List\",\"items\":[" + string.Join(",", items) + "]}");

    /*
     * One namespace object for the inventory. Every container carries a literal environment value, which must never
     * leave the guard. CronJobs nest their pod spec under jobTemplate, Pods carry it directly, the rest under template.
     */
    public static string Item(string kind, string name, string image, bool owned = false, string spec = "", string status = "{}", string initImage = "")
    {
        var annotations = owned ? ",\"annotations\":{\"meta.helm.sh/release-name\":\"kubernetes-production\"}" : string.Empty;
        var init = initImage.Length == 0 ? string.Empty : "\"initContainers\":[{\"name\":\"migrator\",\"image\":\"" + initImage + "\"}],";
        var podSpec = "{" + init + "\"containers\":[{\"name\":\"main\",\"image\":\"" + image + "\",\"env\":[{\"name\":\"ConnectionStrings__Db\",\"value\":\"" + SecretLiteral + "\"}]}]}";
        var specBody = kind switch
        {
            "Pod" => podSpec[1..^1],
            "CronJob" => "\"jobTemplate\":{\"spec\":{\"template\":{\"spec\":" + podSpec + "}}}",
            _ => "\"template\":{\"spec\":" + podSpec + "}",
        };
        return "{\"kind\":\"" + kind + "\",\"metadata\":{\"name\":\"" + name + "\"" + annotations + "},\"spec\":{" + (spec.Length == 0 ? string.Empty : spec + ",") + specBody + "},\"status\":" + status + "}";
    }

    // A healthy, current namespace: nothing in it is a trace, including the revision history and finished work it keeps.
    public static string[] CleanInventory() =>
    [
        Item("Deployment", "web", "ghcr.io/example/web:2", owned: true, spec: "\"replicas\":2", initImage: "ghcr.io/example/migrator:2"),
        Item("CronJob", "schema-confirm", "ghcr.io/example/migrator:2", owned: true, spec: "\"suspend\":true"),
        // The same third-party image at two tags inside the release is not an older release.
        Item("Deployment", "cache-a", "redis:7", owned: true, spec: "\"replicas\":1"),
        Item("StatefulSet", "cache-b", "docker.io/library/redis:6", owned: true, spec: "\"replicas\":1"),
        Item("ReplicaSet", "web-2", "ghcr.io/example/web:2", spec: "\"replicas\":2", status: "{\"replicas\":2}"),
        Item("ReplicaSet", "web-1", "ghcr.io/example/web:1", spec: "\"replicas\":0", status: "{\"replicas\":0}"),
        Item("Pod", "web-2-a", "ghcr.io/example/web:2", status: "{\"phase\":\"Running\"}"),
        Item("Pod", "web-1-done", "ghcr.io/example/web:1", status: "{\"phase\":\"Succeeded\"}"),
        Item("Pod", "web-1-crashed", "ghcr.io/example/web:1", status: "{\"phase\":\"Failed\"}"),
        Item("Pod", "redis-client", "index.docker.io/library/redis:7", status: "{\"phase\":\"Running\"}"),
        Item("Pod", "unrelated-1", "nginx:1", status: "{\"phase\":\"Running\"}"),
        Item("Job", "schema-confirm-old", "ghcr.io/example/migrator:1", status: "{\"conditions\":[{\"type\":\"Complete\",\"status\":\"True\"}]}"),
        Item("Job", "migrate-failed-old", "ghcr.io/example/migrator:1", status: "{\"conditions\":[{\"type\":\"Failed\",\"status\":\"True\"}]}"),
    ];

    // One healthy Deployment owned by the release, plus an unrelated one that must be ignored.
    public void Workloads(string release, bool healthy, bool afterRollback = false)
    {
        var status = healthy
            ? """{"observedGeneration":2,"replicas":2,"updatedReplicas":2,"availableReplicas":2}"""
            : """{"observedGeneration":2,"replicas":3,"updatedReplicas":1,"availableReplicas":2}""";
        Write(afterRollback ? "workloads-after-rollback.json" : "workloads.json", """
            {"items":[
              {"kind":"Deployment","metadata":{"name":"web","generation":2,"annotations":{"meta.helm.sh/release-name":"@RELEASE@"}},
               "spec":{"replicas":2,"selector":{"matchLabels":{"app":"web"}}},"status":@STATUS@},
              {"kind":"Deployment","metadata":{"name":"unrelated","generation":1,"annotations":{"meta.helm.sh/release-name":"other"}},
               "spec":{"replicas":1,"selector":{"matchLabels":{"app":"unrelated"}}},"status":{"observedGeneration":1,"replicas":0,"updatedReplicas":0,"availableReplicas":0}}
            ]}
            """.Replace("@RELEASE@", release, StringComparison.Ordinal).Replace("@STATUS@", status, StringComparison.Ordinal));
    }

    // The new ReplicaSet's pod crash-loops while the old pod keeps serving, as with maxUnavailable: 0.
    public void BreakDeployment(string name, bool healedByRollback = false, string release = "kubernetes-production")
    {
        Workloads(release, healthy: false);
        Write($"rollout-exit-{name}", "1");
        if (healedByRollback)
        {
            Workloads(release, healthy: true, afterRollback: true);
        }
        else
        {
            Write($"rollout-exit-after-{name}", "1");
        }

        Write("pods.json", """
            {"items":[
              {"metadata":{"name":"web-old","labels":{"app":"web"}},"spec":{"containers":[{"name":"web"}]},
               "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}},
              {"metadata":{"name":"web-new","labels":{"app":"web"}},
               "spec":{"initContainers":[{"name":"migrator"}],"containers":[{"name":"web","env":[{"name":"ConnectionStrings__Db","value":"@SECRET@"}]}]},
               "status":{"phase":"Running","conditions":[{"type":"Ready","status":"False"}]}},
              {"metadata":{"name":"unrelated-1","labels":{"app":"unrelated"}},"spec":{"containers":[{"name":"main"}]},
               "status":{"phase":"Pending","conditions":[{"type":"Ready","status":"False"}]}}
            ]}
            """.Replace("@SECRET@", SecretLiteral, StringComparison.Ordinal));
    }

    public GuardResult Guard(string phase, string deployOutcome = "", string timeout = "15m", string rollbackOnFailure = "true", string release = "", bool exclusive = false, string confirmCronJob = "schema-confirm")
    {
        var outputFile = Path.Combine(root, $"output-{Guid.NewGuid():N}.txt");
        var summaryFile = Path.Combine(root, $"summary-{Guid.NewGuid():N}.md");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "run", "--file", guardScript, "--no-launch-profile" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["FAKE_K8S_DIR"] = FakeDir;
        start.Environment["DEPLOY_GUARD_PHASE"] = phase;
        start.Environment["DEPLOY_GUARD_NAMESPACE"] = "example-production";
        start.Environment["DEPLOY_GUARD_ASPIRE_ENVIRONMENT"] = "Kubernetes-Production";
        start.Environment["DEPLOY_GUARD_RELEASE_NAME"] = release;
        start.Environment["DEPLOY_GUARD_ROLLOUT_TIMEOUT"] = timeout;
        start.Environment["DEPLOY_GUARD_ROLLBACK_ON_FAILURE"] = rollbackOnFailure;
        start.Environment["DEPLOY_GUARD_EXCLUSIVE"] = exclusive ? "true" : "false";
        start.Environment["DEPLOY_GUARD_SCHEMA_ROLLBACK_CRONJOB"] = "schema-rollback";
        start.Environment["DEPLOY_GUARD_SCHEMA_CONFIRM_CRONJOB"] = confirmCronJob;
        start.Environment["DEPLOY_GUARD_REPORT_DIR"] = "artifacts/deploy-report";
        start.Environment["DEPLOY_GUARD_DEPLOY_OUTCOME"] = deployOutcome;
        start.Environment["DEPLOY_GUARD_SHIM_RECORD"] = ShimRecord;
        start.Environment["DEPLOY_GUARD_JOB_TIMEOUT_MINUTES"] = "60";
        start.Environment["DEPLOY_GUARD_POLL_SECONDS"] = "0.2";
        start.Environment["GITHUB_WORKSPACE"] = Path.Combine(root, "workspace");
        start.Environment["RUNNER_TEMP"] = RunnerTemp;
        start.Environment["GITHUB_RUN_ID"] = "4242";
        start.Environment["GITHUB_RUN_ATTEMPT"] = "1";
        start.Environment["GITHUB_OUTPUT"] = outputFile;
        start.Environment["GITHUB_STEP_SUMMARY"] = summaryFile;
        var output = ProcessOutput.Run(start);
        return new GuardResult(
            output.ExitCode,
            output.Stdout + output.Stderr,
            File.Exists(outputFile) ? File.ReadAllLines(outputFile) : [],
            File.Exists(summaryFile) ? File.ReadAllText(summaryFile) : string.Empty);
    }

    static string Json((int Revision, string Status)[] entries) =>
        "[" + string.Join(",", entries.Select(entry => $$"""{"revision":{{entry.Revision}},"updated":"2026-11-19T10:00:00Z","status":"{{entry.Status}}","chart":"app-1.0.0","app_version":"1.0.0","description":"{{(entry.Status == "failed" ? FailedDescription : "fixture")}}"}""")) + "]";

    // JSON-escaped description of a failed revision, shaped like real Helm 4.3.1 output for a field conflict.
    const string FailedDescription = """Upgrade \"fixture\" failed: conflict occurred while applying object example-production/web apps/v1, Kind=Deployment: Apply failed with 1 conflict: conflict with \"kubectl-edit\" using apps/v1:\n- .spec.replicas""";
}

sealed record GuardResult(int ExitCode, string Log, string[] Outputs, string Summary)
{
    public string Output(string name)
    {
        var prefix = name + "=";
        return Outputs.LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? string.Empty;
    }
}

sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr)
{
    public static ProcessOutput Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{start.FileName} did not finish within 5 minutes.");
        }

        process.WaitForExit();
        return new ProcessOutput(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

static class FakeCli
{
    /*
     * The `helm list` and `helm history` JSON shapes are those of real Helm 4.3.1 (tests/live/helm-release-guard-kind.sh):
     * `list` gives the revision as a string, `history` as a number, and a missing release makes `history` exit 1.
     * One script installed as both `helm` and `kubectl`. It records `<tool> <args>` in calls.log and answers from
     * files in FAKE_K8S_DIR. A successful `helm rollback` flips the fake cluster to its "-after-rollback" files.
     * `kubectl scale` writes replicas-<name>; while a Deployment is at 0 its pods are gone unless pods-stuck exists.
     * A rollback re-applies the chart's replica counts (deletes replicas-*) unless rollback-keeps-scale exists, which
     * is how real Helm 4.3.1 behaves after `kubectl scale` (tests/live/helm-release-guard-kind.sh).
     * The schema confirm reads confirm-cronjob.json (absent = no CronJob) and inventory.json (the one multi-kind list
     * the trace check makes); confirm-cronjob-exit and inventory-exit answer like a forbidden RBAC read.
     */
    public const string Script = """
        #!/usr/bin/env bash
        set -u
        dir="${FAKE_K8S_DIR:?}"
        tool="$(basename "$0")"
        args="$*"
        printf '%s %s\n' "$tool" "$args" >> "$dir/calls.log"

        exit_code() { if [[ -f "$dir/$1" ]]; then cat "$dir/$1"; else echo 0; fi; }
        rolled_back() { [[ -f "$dir/rolled-back" ]]; }

        if [[ "$tool" == "helm" ]]; then
          case "$args" in
            "list "*)
              if [[ -f "$dir/helm-list-exit" ]]; then echo "Error: Kubernetes cluster unreachable" >&2; exit 1; fi
              if [[ -f "$dir/history.json" ]]; then echo '[{"name":"fixture","namespace":"example-production","revision":"1","updated":"2026-11-19 20:23:43.452234259 +0100 CET","status":"deployed","chart":"app-1.0.0","app_version":"1.0.0"}]'; else echo '[]'; fi ;;
            "history "*"--output json"*)
              if [[ ! -f "$dir/history.json" ]]; then echo "Error: release: not found" >&2; exit 1; fi
              cat "$dir/history.json" ;;
            "history "*) echo "REVISION STATUS (fixture table)" ;;
            "status "*) echo "STATUS: fixture" ;;
            "get metadata "*)
              if [[ ! -f "$dir/apply-method" ]]; then echo "Error: release: not found" >&2; exit 1; fi
              echo "{\"name\":\"fixture\",\"revision\":3,\"status\":\"deployed\",\"applyMethod\":\"$(cat "$dir/apply-method")\"}" ;;
            "rollback "*)
              code="$(exit_code rollback-exit)"
              if [[ "$code" == "0" ]]; then
                touch "$dir/rolled-back"
                if [[ ! -f "$dir/rollback-keeps-scale" ]]; then rm -f "$dir"/replicas-*; fi
                if [[ -f "$dir/history-after-rollback.json" ]]; then cp "$dir/history-after-rollback.json" "$dir/history.json"; fi
                echo "Rollback was a success! Happy Helming!"
              else
                echo "Error: fixture rollback failure" >&2
              fi
              exit "$code" ;;
            "upgrade "*) exit "$(exit_code upgrade-exit)" ;;
            *) echo "fixture helm" ;;
          esac
          exit 0
        fi

        case "$args" in
          "get cronjob schema-confirm "*)
            if [[ -f "$dir/confirm-cronjob-exit" ]]; then echo 'Error from server (Forbidden): cronjobs.batch "schema-confirm" is forbidden' >&2; exit 1; fi
            if [[ -f "$dir/confirm-cronjob.json" ]]; then cat "$dir/confirm-cronjob.json"; fi ;;
          "get cronjob "*) if [[ -f "$dir/cronjob.json" ]]; then cat "$dir/cronjob.json"; fi ;;
          "get deployments,statefulsets,daemonsets,replicasets,cronjobs,jobs,pods "*)
            if [[ -f "$dir/inventory-exit" ]]; then echo 'Error from server (Forbidden): replicasets.apps is forbidden: User "deployer" cannot list resource "replicasets"' >&2; exit 1; fi
            if [[ -f "$dir/inventory.json" ]]; then cat "$dir/inventory.json"; else echo '{"items":[]}'; fi ;;
          "get deployments,statefulsets,daemonsets,jobs "*)
            if rolled_back && [[ -f "$dir/workloads-after-rollback.json" ]]; then cat "$dir/workloads-after-rollback.json"; else cat "$dir/workloads.json"; fi ;;
          "get pods "*)
            if [[ ! -f "$dir/pods-stuck" ]] && grep -qx 0 "$dir"/replicas-* 2> /dev/null; then echo '{"items":[]}'
            elif [[ -f "$dir/pods.json" ]]; then cat "$dir/pods.json"; else echo '{"items":[]}'; fi ;;
          "get deployment "*"jsonpath={.spec.replicas}"*)
            if [[ -f "$dir/replicas-$3" ]]; then cat "$dir/replicas-$3"; else echo 2; fi ;;
          "scale deployment/"*)
            name="${2#deployment/}"
            replicas=""
            for arg in "$@"; do case "$arg" in --replicas=*) replicas="${arg#--replicas=}" ;; esac; done
            if [[ "$replicas" == "0" && -f "$dir/scale-zero-exit" ]]; then echo "error: fixture scale failure" >&2; exit 1; fi
            printf '%s\n' "$replicas" > "$dir/replicas-$name"
            echo "deployment.apps/$name scaled" ;;
          "get job schema-confirm-"*"--output json"*)
            if [[ -f "$dir/confirm-job-status.json" ]]; then cat "$dir/confirm-job-status.json"; else echo '{"status":{"conditions":[{"type":"Complete","status":"True"}]}}'; fi ;;
          "get job "*"--output json"*)
            if [[ -f "$dir/job-status.json" ]]; then cat "$dir/job-status.json"; else echo '{"status":{"conditions":[{"type":"Complete","status":"True"}]}}'; fi ;;
          "get "*) echo "fixture table" ;;
          "describe pod "*)
            printf '%s\n' "Name:         fixture" "Containers:" "  web:" "    Image:      example/web:2" "    Environment:" \
              "      ConnectionStrings__Db:  Host=db;Password=fixture-not-a-real-secret" \
              "      Discord__Token:         <set to the key 'token' in secret 'bot'>" "    Mounts:" "      /tmp from tmp (rw)" "Events:  <none>" ;;
          "logs "*"--previous"*)
            case "$args" in *"--container web "*) echo "previous crash" ;; *) echo "Error: previous terminated container not found" >&2; exit 1 ;; esac ;;
          "logs job/schema-confirm-"*)
            printf '%s\n' "2026-01-01T00:00:01.000000000Z checking release" "2026-01-01T00:00:02.000000000Z info: Fixture.Migrator[7]" "2026-01-01T00:00:03.000000000Z       Release 4 rollout confirmed." ;;
          "logs job/"*)
            printf '%s\n' "2026-01-01T00:00:01.000000000Z sql noise 1" "2026-01-01T00:00:02.000000000Z sql noise 2" "2026-01-01T00:00:03.000000000Z sql noise 3" \
              "2026-01-01T00:00:04.000000000Z warn: Fixture.Migrator[1]" "2026-01-01T00:00:05.000000000Z       Rolled back release 2: reverted 7 migration(s)." "" \
              "2026-01-01T00:00:06.000000000Z info: Fixture.Migrator[5]" "2026-01-01T00:00:07.000000000Z       Release schema rollback completed: RolledBack." ;;
          "logs "*) echo "fixture log line" ;;
          "rollout status deployment/"*)
            name="${3#deployment/}"
            if rolled_back; then code="$(exit_code "rollout-exit-after-$name")"; else code="$(exit_code "rollout-exit-$name")"; fi
            if [[ "$code" != "0" ]]; then echo "error: timed out waiting for the condition" >&2; fi
            exit "$code" ;;
          "create job "*)
            echo '{"apiVersion":"batch/v1","kind":"Job","metadata":{"name":"'"$3"'","ownerReferences":[{"kind":"CronJob","name":"schema-rollback","uid":"fixture"}]},"spec":{"template":{"spec":{"initContainers":[{"name":"wait","image":"example/wait:1"}],"containers":[{"name":"migrator","image":"example/migrator:2","env":[{"name":"X","value":"schema-env-literal"},{"name":"ARKANIS_DEPLOY_STARTED_AT","value":"1999-01-01T00:00:00Z"}]},{"name":"sidecar","image":"example/sidecar:1","env":[{"name":"ARKANIS_DEPLOY_STARTED_AT","valueFrom":{"fieldRef":{"fieldPath":"metadata.name"}}}]}]}}}}' ;;
          "create --filename "*) cp "$3" "$dir/created-job.json"; echo "job created"; exit "$(exit_code create-exit)" ;;
          "delete job "*) echo "job deleted" ;;
          *) echo "fixture kubectl" ;;
        esac
        exit 0
        """;
}
