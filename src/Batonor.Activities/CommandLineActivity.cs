using Batonor.Abstractions;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Batonor.Activities;

/// <summary>
/// Built-in <c>commandline</c> activity: runs an external process described by the node config
/// (<c>executable</c>/<c>args</c>/<c>workDir</c>/<c>captureStdout</c>) and returns the captured stdout.
/// <b>No sandboxing is performed</b> — the host is responsible for argument validation and isolation.
/// </summary>
public sealed class CommandLineActivity : IActivity
{
    public async ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
    {
        var input = context.Input as JsonObject ?? new JsonObject();
        var executable = input["executable"]?.GetValue<string>()
            ?? throw new BatonorException("CommandLine activity requires an 'executable'.");

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = input["captureStdout"]?.GetValue<bool>() ?? false,
            RedirectStandardError = false,
        };

        if (input["workDir"]?.GetValue<string>() is { Length: > 0 } workDir)
        {
            psi.WorkingDirectory = workDir;
        }

        if (input["args"] is JsonArray args)
        {
            foreach (var arg in args)
            {
                if (arg is JsonValue jv && jv.TryGetValue<string>(out var a))
                {
                    psi.ArgumentList.Add(a);
                }
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = "";
        if (psi.RedirectStandardOutput)
        {
            stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return stdout;
    }
}
