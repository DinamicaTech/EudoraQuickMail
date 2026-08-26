using EudoraImporter;

if (args.Length > 0 && args[0].Equals("benchmark", StringComparison.OrdinalIgnoreCase))
    return await SearchBenchmark.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("native-import", StringComparison.OrdinalIgnoreCase))
    return await NativeProfileImportCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("native-search", StringComparison.OrdinalIgnoreCase))
    return await NativeSearchCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
    return await MigrationCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("local-folder-tree", StringComparison.OrdinalIgnoreCase))
    return LocalFolderTreeCommand.Run(args[1..]);

return await ImportCommand.RunAsync(args);
