// Test-only subprocess. It never connects to ADB or an Android device.
if (args.Length != 3 || args[0] != "-s" || args[1] != "emulator-5554") return 90;
var command = args[2];
if (command is not ("root" or "unroot" or "provision")) return 91;
var mode = File.ReadAllText("mode").Trim();
File.AppendAllText("calls", command + "\n");
File.WriteAllText(command + ".started", Environment.ProcessId.ToString());
if ((command == "root" && mode is "cancel-root" or "timeout-root") ||
    (command == "provision" && mode == "cancel-provision"))
    await Task.Delay(TimeSpan.FromSeconds(30));
if (command == "provision" && mode == "double-failure")
{
    Console.Error.WriteLine("fake provisioning failed");
    return 12;
}
if (command == "unroot" && mode == "double-failure")
{
    Console.Error.WriteLine("fake unroot failed");
    return 13;
}
Console.WriteLine(command == "root" ? "0" : "2000");
return 0;
