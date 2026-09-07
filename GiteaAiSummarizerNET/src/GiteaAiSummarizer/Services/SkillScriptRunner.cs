// Copyright (c) Microsoft. All rights reserved.

// Sample subprocess-based skill script runner.
// Executes file-based skill scripts as local subprocesses.
// This is provided for demonstration purposes only.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Executes file-based skill scripts as local subprocesses.
/// </summary>
/// <remarks>
/// This runner uses the script's absolute path and converts the arguments
/// to CLI arguments. When the LLM sends a JSON array, each element is used
/// as a positional argument. It is intended for demonstration purposes only.
/// </remarks>
internal static class SkillScriptRunner
{
    /// <summary>
    /// Runs a skill script as a local subprocess.
    /// </summary>
    public static async Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(script.FullPath))
        {
            return $"Error: Script file not found: {script.FullPath}";
        }

        string extension = Path.GetExtension(script.FullPath);
        string? interpreter = extension switch
        {
            ".py" => File.Exists("/usr/bin/python3") || !OperatingSystem.IsWindows() ? "python3" : "python",
            ".js" => "node",
            ".sh" => "bash",
            ".ps1" => "pwsh",
            _ => null,
        };

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script.FullPath) ?? ".",
        };
        
        // Force UTF-8 for Python on Windows
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        if (interpreter is not null)
        {
            startInfo.FileName = interpreter;
            startInfo.ArgumentList.Add(script.FullPath);

            var logger = serviceProvider
                ?.GetService<ILoggerFactory>()
                ?.CreateLogger(nameof(SkillScriptRunner));
            logger?.LogInformation(
                "Skill Execution: Running file-based skill '{SkillName}' using {Interpreter}...",
                script.Name,
                interpreter
            );
        }
        else
        {
            startInfo.FileName = script.FullPath;
            var logger = serviceProvider
                ?.GetService<ILoggerFactory>()
                ?.CreateLogger(nameof(SkillScriptRunner));
            logger?.LogInformation(
                "Skill Execution: Running file-based skill '{SkillName}' directly...",
                script.Name
            );
        }

        // Bound how long we'll wait for the process, independent of whatever cancellation token the
        // caller passes in. ProcessAsync is invoked with CancellationToken.None from both webhook
        // endpoints, so without this a stuck script would hang the run forever.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var waitToken = linkedCts.Token;

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return $"Error: Failed to start process for script '{script.Name}'.";
            }

            // Write JSON arguments to Stdin channel. Stdin must be closed unconditionally — a script
            // that reads from stdin (e.g. via sys.stdin.read()) blocks waiting for EOF, and if the
            // caller omits arguments this close is the only thing that ever unblocks it.
            if (arguments.HasValue)
            {
                await process.StandardInput.WriteAsync(arguments.Value.GetRawText());
                await process.StandardInput.FlushAsync();
            }
            process.StandardInput.Close();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(waitToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(waitToken);

            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrEmpty(error))
            {
                output += $"\nStderr:\n{error}";
            }

            if (process.ExitCode != 0)
            {
                output += $"\nScript exited with code {process.ExitCode}";
            }

            return string.IsNullOrEmpty(output) ? "(no output)" : output.Trim();
        }
        // Our own timeout, not the caller's cancellation — kill the process and report it like every
        // other failure in this method (a string), not as an exception. Letting this propagate would
        // both leave a zombie process (no kill on this path otherwise) and could fail the whole PR run
        // via the agent's function-invocation loop — worse than the hang it replaces.
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            process?.Kill(entireProcessTree: true);
            return $"Error: Script '{script.Name}' timed out after 2 minutes.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Kill the process on cancellation to avoid leaving orphaned subprocesses.
            process?.Kill(entireProcessTree: true);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: Failed to execute script '{script.Name}': {ex.Message}";
        }
        finally
        {
            process?.Dispose();
        }
    }
}