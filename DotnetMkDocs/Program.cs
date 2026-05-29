using System;
using System.IO;

namespace DotnetMkDocs;
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Error: No files provided. Drag and drop one or more .dll files onto this executable.");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        DocGenerator generator = new DocGenerator();

        foreach (var fileToProcess in args)
        {
            // Verify it's actually a DLL
            if (Path.GetExtension(fileToProcess).ToLower() != ".dll")
            {
                Console.WriteLine($"Skipping non-dll file: {Path.GetFileName(fileToProcess)}");
                continue;
            }

            string fullDllPath = Path.GetFullPath(fileToProcess);
            string dllDirectory = Path.GetDirectoryName(fullDllPath)!;
            string assemblyName = Path.GetFileNameWithoutExtension(fullDllPath);
            string expectedXmlPath = Path.Combine(dllDirectory, $"{assemblyName}.xml");

            // Verify the matching XML file exists
            if (!File.Exists(expectedXmlPath))
            {
                Console.WriteLine($"Error: Could not find matching XML documentation for {assemblyName}.dll");
                Console.WriteLine($"Expected it at: {expectedXmlPath}");
                continue;
            }

            // Create a 'docs' directory right next to the source DLL
            string outputDirectory = Path.Combine(dllDirectory, "docs");

            Console.WriteLine($"Processing: {assemblyName}.dll...");

            try
            {
                generator.GenerateForAssembly(fullDllPath, expectedXmlPath, assemblyName, outputDirectory);
                Console.WriteLine($"Successfully generated docs for {assemblyName}!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to process {assemblyName}: {ex.Message}");
            }
            Console.WriteLine("------------------------------------------------");
        }

        string finalYamlPath = Path.Combine(Path.GetDirectoryName(args[0])!, "docs", "nav-snippet.yml");
        generator.ExportNavYaml(finalYamlPath, "api/");

        Console.WriteLine($"\nNavigation YAML generated at: {finalYamlPath}");
        Console.WriteLine("\nAll processing complete. Press any key to close this window...");
        Console.ReadKey();
    }
}