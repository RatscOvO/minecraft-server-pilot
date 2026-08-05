using System.Diagnostics;

if (args.Length == 1 && args[0] == "-version")
{
    Console.Error.WriteLine("java version \"1.8.0_999\"");
    return 0;
}

var marker = Path.Combine(AppContext.BaseDirectory, "memory-failure-injected.marker");
if (!File.Exists(marker))
{
    File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
    Console.Error.WriteLine("Error occurred during initialization of VM");
    Console.Error.WriteLine("Could not reserve enough space for object heap");
    return 1;
}

var realJava = Environment.GetEnvironmentVariable("PILOT_TEST_REAL_JAVA");
if (string.IsNullOrWhiteSpace(realJava) || !File.Exists(realJava))
{
    Console.Error.WriteLine("PILOT_TEST_REAL_JAVA is missing or invalid.");
    return 2;
}

using var process = new Process
{
    StartInfo = new ProcessStartInfo(realJava)
    {
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true
    }
};
foreach (var argument in args)
    process.StartInfo.ArgumentList.Add(argument);
process.OutputDataReceived += (_, eventArgs) =>
{
    if (eventArgs.Data is not null)
        Console.Out.WriteLine(eventArgs.Data);
};
process.ErrorDataReceived += (_, eventArgs) =>
{
    if (eventArgs.Data is not null)
        Console.Error.WriteLine(eventArgs.Data);
};
process.Start();
process.BeginOutputReadLine();
process.BeginErrorReadLine();
_ = Task.Run(async () =>
{
    while (!process.HasExited)
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null)
            break;
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
    }
});
await process.WaitForExitAsync();
return process.ExitCode;
