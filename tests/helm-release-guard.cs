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

        s.History((3, "superseded"), (4, "deployed"));
        var result = s.Guard("finalize", "success");
        Check(result.ExitCode == 0, "a verified deployment must end green", result.Log);
        Check(result.Output("outcome") == "deployed", "outcome must be deployed");
        Check(result.Output("previous-revision") == "3" && result.Output("new-revision") == "4" && result.Output("current-revision") == "4",
            "success must report previous and new revision");
        Check(s.Called("kubectl rollout status deployment/web"), "success must still verify every Deployment rollout");
        Check(!s.Called("helm rollback"), "success must not roll back");
        Check(result.Summary.Contains($"helm rollback {Release} 3 --namespace example-production --wait", StringComparison.Ordinal),
            "the summary must carry the manual rollback reference");
        Check(File.Exists(Path.Combine(s.ReportDir, "deploy-report.json")), "the report must be written to the artifact directory");
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
        var result = s.Guard("finalize", "failure");
        Check(result.ExitCode != 0, "the job must end red even though the rollback succeeded");
        Check(result.Output("outcome") == "rolled-back", "outcome must be rolled-back", result.Log);
        Check(result.Output("previous-revision") == "3" && result.Output("new-revision") == "4" && result.Output("current-revision") == "5",
            "rollback must report previous, failed, and live revision");
        Check(result.Output("schema-rollback") == "succeeded", "the schema hook must have run", result.Log);
        Check(result.Summary.Contains("environment restored to revision 3", StringComparison.Ordinal), "the summary must say the environment was restored");
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
        Check(File.Exists(Path.Combine(s.ReportDir, "diagnostics", "after-rollback", "helm-history.txt")), "post-rollback state must be captured");
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
        Check(s.Guard("capture").ExitCode != 0, "capture must fail closed when the release cannot be read");
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

        File.Delete(Path.Combine(s.FakeDir, "calls.log"));
        Check(Shim(s, realHelm, "version", "--short").ExitCode == 0 && Shim(s, realHelm, "rollback", Release, "3", "--namespace", "example-production").ExitCode == 0,
            "other subcommands pass");
        Check(s.Calls == $"helm version --short\nhelm rollback {Release} 3 --namespace example-production\n", "other subcommands must pass through untouched", s.Calls);

        s.Write("upgrade-exit", "1");
        Check(Shim(s, realHelm, "upgrade", "--install", Release, "chart", "--namespace", "example-production").ExitCode == 1, "the shim must return Helm's exit code");
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

    // uid + generation are what the guard compares: a chart resource keeps its uid and bumps the generation,
    // a Helm hook with before-hook-creation is recreated (new uid) and starts at generation 1 again.
    public void CronJob(int generation, string uid = "uid-a") =>
        Write("cronjob.json", """{"kind":"CronJob","metadata":{"name":"schema-rollback","uid":"@UID@","generation":@GENERATION@},"spec":{"suspend":true}}"""
            .Replace("@UID@", uid, StringComparison.Ordinal)
            .Replace("@GENERATION@", generation.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

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

    public GuardResult Guard(string phase, string deployOutcome = "", string timeout = "15m", string rollbackOnFailure = "true", string release = "")
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
        start.Environment["DEPLOY_GUARD_SCHEMA_ROLLBACK_CRONJOB"] = "schema-rollback";
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
        "[" + string.Join(",", entries.Select(entry => $$"""{"revision":{{entry.Revision}},"updated":"2026-11-19T10:00:00Z","status":"{{entry.Status}}","chart":"app-1.0.0","app_version":"1.0.0","description":"fixture"}""")) + "]";
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
     * One script installed as both `helm` and `kubectl`. It records `<tool> <args>` in calls.log and answers from
     * files in FAKE_K8S_DIR. A successful `helm rollback` flips the fake cluster to its "-after-rollback" files.
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
              if [[ -f "$dir/history.json" ]]; then echo '[{"name":"fixture","revision":"1","status":"deployed"}]'; else echo '[]'; fi ;;
            "history "*"--output json"*)
              if [[ ! -f "$dir/history.json" ]]; then echo "Error: release: not found" >&2; exit 1; fi
              cat "$dir/history.json" ;;
            "history "*) echo "REVISION STATUS (fixture table)" ;;
            "status "*) echo "STATUS: fixture" ;;
            "rollback "*)
              code="$(exit_code rollback-exit)"
              if [[ "$code" == "0" ]]; then
                touch "$dir/rolled-back"
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
          "get cronjob "*) if [[ -f "$dir/cronjob.json" ]]; then cat "$dir/cronjob.json"; fi ;;
          "get deployments,statefulsets,daemonsets,jobs "*)
            if rolled_back && [[ -f "$dir/workloads-after-rollback.json" ]]; then cat "$dir/workloads-after-rollback.json"; else cat "$dir/workloads.json"; fi ;;
          "get pods "*) if [[ -f "$dir/pods.json" ]]; then cat "$dir/pods.json"; else echo '{"items":[]}'; fi ;;
          "get job "*"--output json"*)
            if [[ -f "$dir/job-status.json" ]]; then cat "$dir/job-status.json"; else echo '{"status":{"conditions":[{"type":"Complete","status":"True"}]}}'; fi ;;
          "get "*) echo "fixture table" ;;
          "describe pod "*)
            printf '%s\n' "Name:         fixture" "Containers:" "  web:" "    Image:      example/web:2" "    Environment:" \
              "      ConnectionStrings__Db:  Host=db;Password=fixture-not-a-real-secret" \
              "      Discord__Token:         <set to the key 'token' in secret 'bot'>" "    Mounts:" "      /tmp from tmp (rw)" "Events:  <none>" ;;
          "logs "*"--previous"*)
            case "$args" in *"--container web "*) echo "previous crash" ;; *) echo "Error: previous terminated container not found" >&2; exit 1 ;; esac ;;
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
