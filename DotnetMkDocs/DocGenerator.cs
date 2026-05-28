using LoxSmoke.DocXml;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace DotnetMkDocs;

public class DocGenerator
{
    // gets the markdown link path relative to the current file.
    string GetTypeReferenceCanonical(Type currentType, Type targetType)
    {
        // Safety check for generic parameters (like T) that don't have namespaces
        if (string.IsNullOrEmpty(currentType.Namespace) || string.IsNullOrEmpty(targetType.Namespace))
            return $"{targetType.Name.ToLower()}.md";

        // If they are in the exact same namespace, just link the file directly
        if (currentType.Namespace == targetType.Namespace)
            return $"{targetType.Name.ToLower()}.md";

        string[] sourceParts = currentType.Namespace.Split('.');
        string[] targetParts = targetType.Namespace.Split('.');

        // Find how many namespace segments they share (the common root)
        int commonCount = 0;
        int minLength = Math.Min(sourceParts.Length, targetParts.Length);

        while (commonCount < minLength && sourceParts[commonCount] == targetParts[commonCount])
        {
            commonCount++;
        }
        int dirsUp = sourceParts.Length - commonCount;
        string upPath = string.Concat(Enumerable.Repeat("../", dirsUp));
        string downPath = string.Join("/", targetParts.Skip(commonCount)).ToLower();
        if (!string.IsNullOrEmpty(downPath))
        {
            downPath += "/";
        }
        return $"{upPath}{downPath}{targetType.Name.ToLower()}.md";
    }
    string GetTypeReference(Type currentType, Type type)
    {
        if (type.IsByRef)
        {
            return GetTypeReference(currentType, type.GetElementType()!);
        }
        else if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return $"[{elementType.Name}[]]({GetTypeReferenceCanonical(currentType, elementType)})";
        }
        else if (type.IsGenericTypeDefinition || type.IsGenericType)
        {
            // Recursively call GetTypeReference to handle nested generics
            var genericArgs = string.Join(", ", type.GenericTypeArguments.Select(g => GetTypeReference(currentType, g)));

            return $"[{type.Name.Substring(0, type.Name.LastIndexOf('`'))}&lt;{genericArgs}&gt;]({GetTypeReferenceCanonical(currentType, type)})";
        }
        return $"[{type.Name}]()";
    }

    public void GenerateForAssembly(string dllFilePath, string xmlFilePath, string assemblyName, string rootOutputDirectory)
    {
        string outputFolder = Path.Combine(rootOutputDirectory, assemblyName);
        Directory.CreateDirectory(outputFolder);

        DocXmlReader reader = new DocXmlReader(xmlFilePath);

        string dllDirectory = Path.GetDirectoryName(dllFilePath)!;

        var resolverPaths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
        resolverPaths.AddRange(Directory.GetFiles(dllDirectory, "*.dll"));

        var resolver = new PathAssemblyResolver(resolverPaths);

        using var context = new MetadataLoadContext(resolver);

        Assembly assembly = context.LoadFromAssemblyPath(dllFilePath);

        foreach (var type in assembly.ExportedTypes)
        {
            if (!type.IsVisible) continue;

            string className = type.Name;

            var xmlType = reader.GetTypeComments(type);

            StringBuilder fieldBuilder = new();
            StringBuilder propertyBuilder = new();
            StringBuilder methodBuilder = new();

            foreach (var field in type.GetFields())
            {
                var xmlField = reader.GetMemberComment(field)?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";
                fieldBuilder.AppendLine($"| `{field.Name}` | {GetTypeReference(type, field.FieldType)} | {xmlField} |");
            }

            foreach (var property in type.GetProperties())
            {
                var xmlProperty = reader.GetMemberComment(property)?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";
                propertyBuilder.AppendLine($"| `{property.Name}` | {GetTypeReference(type, property.PropertyType)} | {xmlProperty} |");
            }

            // Loop through the methods inside this class
            foreach (var method in type.GetMethods())
            {
                var xmlMethod = reader.GetMethodComments(method);
                var parameters = method.GetParameters();

                var GetParamModifiers = (ParameterInfo p) =>
                {
                    if (p.IsOut) return "out ";
                    if (p.IsIn) return "in ";
                    if (p.ParameterType.IsByRef) return "ref ";
                    return string.Empty;
                };

                var paramSignatures = parameters.Select(p =>
                    $"{GetParamModifiers(p)}{GetTypeReference(type, p.ParameterType)} {p.Name}");

                string joinedParams = string.Join(", ", paramSignatures);

                methodBuilder.AppendLine($"### `{method.Name}({joinedParams})`");
                methodBuilder.AppendLine();

                if (!string.IsNullOrWhiteSpace(xmlMethod.Summary))
                {
                    // Replace newlines so it formats cleanly in MkDocs
                    methodBuilder.AppendLine(xmlMethod.Summary.Replace("\n", " ").Trim());
                    methodBuilder.AppendLine();
                }

                if (parameters.Length > 0)
                {
                    methodBuilder.AppendLine("**Parameters:**");

                    foreach (var p in parameters)
                    {
                        // Try to find the matching <param name="x"> tag from the XML
                        var xmlParam = xmlMethod.Parameters?.FirstOrDefault(x => x.Name == p.Name);
                        string paramDescription = xmlParam?.Text.Trim() ?? "";

                        methodBuilder.AppendLine($"* `{p.Name}` (`{GetTypeReference(type, p.ParameterType)}`): {paramDescription}");
                    }
                    methodBuilder.AppendLine();
                }

                if (method.ReturnType != typeof(void))
                {
                    methodBuilder.AppendLine("**Returns:**");

                    string returnDescription = !string.IsNullOrWhiteSpace(xmlMethod.Returns)
                        ? xmlMethod.Returns.Trim()
                        : "";

                    methodBuilder.AppendLine($"`{GetTypeReference(type, method.ReturnType)}`: {returnDescription}");
                    methodBuilder.AppendLine();
                }

                methodBuilder.AppendLine("---");
            }


            string typeKind = "???";
            if (type.IsEnum)
            {
                typeKind = "enum";
            }
            else if (type.IsInterface)
            {
                typeKind = "interface";
            }
            else if (type.IsClass)
            {
                typeKind = "class";
            }
            else if (type.IsValueType)
            {
                typeKind = "struct";
            }
            string typeDeclarationModifier = "";
            if (type.IsAbstract)
            {
                typeDeclarationModifier += "abstract ";
            }
            if (type.IsSealed)
            {
                typeDeclarationModifier += "sealed ";
            }

            List<string> parents = new();
            Type? parent = type.BaseType;
            while (parent is not null)
            {
                parents.Add(GetTypeReference(type, parent));
                parent = parent.BaseType;
            }
            parents.Reverse();

            string inheritanceBuilder = parents.Count > 0
                ? string.Join(" ➔ ", parents) + " ➔ "
                : "";
            string interfaceBuilder = string.Join(", ", type.GetInterfaces().Select(i => GetTypeReference(type, i)));

            string template = $""""
                    # {type.Name}

                    {xmlType.Summary}

                    ## Definition

                    **Namespace:** `{type.Namespace}`  
                    **Assembly:** `{type.Assembly.FullName}.dll`

                    ```csharp
                    {typeDeclarationModifier}{typeKind} {type.Name}

                    ```

                    ## Inheritance & Interfaces

                    **Inheritance:**

                    `{inheritanceBuilder}` ➔ **{type.Name}**

                    **Implements:**

                    `{interfaceBuilder}`

                    ---

                    ## Fields

                    | Name | Type | Description |
                    | --- | --- | --- |
                    {fieldBuilder}

                    ---

                    ## Properties

                    | Name | Type | Description |
                    | --- | --- | --- |
                    {propertyBuilder}

                    ---

                    ## Methods

                   {methodBuilder}

                    ---
"""";

            string relativeNamespacePath = type.Namespace?.Replace('.', '/') ?? string.Empty;
            string finalDirectory = Path.Combine(outputFolder, relativeNamespacePath);
            Directory.CreateDirectory(finalDirectory);
            File.WriteAllText(Path.Combine(finalDirectory, $"{className}.md"), template);
        }
    }
}
