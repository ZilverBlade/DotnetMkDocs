using DocXml.Reflection;
using LoxSmoke.DocXml;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace DotnetMkDocs;

public class DocGenerator
{
    private string[] GetNamespaceParts(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return Array.Empty<string>();

        string[] rawParts = ns.Split('.');

        // If it's your project structure, bundle the first 3 parts as the root assembly folder
        if (rawParts.Length >= 3 && rawParts[0] == "SDT4" && rawParts[1] == "Managed")
        {
            string rootAsm = $"{rawParts[0]}.{rawParts[1]}.{rawParts[2]}";
            var parts = new List<string> { rootAsm };
            if (rawParts.Length > 3) parts.AddRange(rawParts.Skip(3).Select(p => p.ToLower()));
            return parts.ToArray();
        }

        // Fallback for any other random namespaces
        return rawParts.Select(p => p.ToLower()).ToArray();
    }

    string GetTypeReferenceCanonical(Type currentType, Type targetType)
    {
        string currentNs = currentType.Namespace ?? string.Empty;
        string targetNs = targetType.Namespace ?? string.Empty;

        // Intercept System and Microsoft types and point to official Docs
        if (targetNs.StartsWith("System") || targetNs.StartsWith("Microsoft"))
        {
            string cleanName = targetType.Name.Replace('`', '-').ToLower();
            return $"https://learn.microsoft.com/dotnet/api/{targetNs.ToLower()}.{cleanName}";
        }

        if (currentNs == targetNs)
            return $"./{targetType.Name.ToLower()}.md";

        string[] sourceParts = GetNamespaceParts(currentNs);
        string[] targetParts = GetNamespaceParts(targetNs);

        int commonCount = 0;
        int minLength = Math.Min(sourceParts.Length, targetParts.Length);

        while (commonCount < minLength && sourceParts[commonCount] == targetParts[commonCount])
        {
            commonCount++;
        }

        int dirsUp = sourceParts.Length - commonCount;
        string upPath = dirsUp == 0 ? "./" : string.Concat(Enumerable.Repeat("../", dirsUp));

        string downPath = string.Join("/", targetParts.Skip(commonCount));
        if (!string.IsNullOrEmpty(downPath)) downPath += "/";

        return $"{upPath}{downPath}{targetType.Name.ToLower()}.md";
    }

    string GetTypeReference(NullabilityInfo? nullabilityInfo, Type currentType, Type type, bool link = true)
    {
        if (type.IsGenericParameter) return type.Name;

        string append = "";
        if (nullabilityInfo?.WriteState == NullabilityState.Nullable)
        {
            append = "?";
        }
        else
        {
            Type? nullableUnderlying = Nullable.GetUnderlyingType(type);
            if (nullableUnderlying != null)
            {
                append = "?";
                type = nullableUnderlying;
            }
        }

        if (type.IsByRef)
        {
            return GetTypeReference(nullabilityInfo, currentType, type.GetElementType()!, link);
        }
        else if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            string nolink0 = $"{GetTypeReference(nullabilityInfo, currentType, elementType, false)}[]{append}";
            if (link) return $"[{nolink0}]({GetTypeReferenceCanonical(currentType, elementType)})";
            else return nolink0;
        }
        else if (type.IsGenericTypeDefinition || type.IsGenericType)
        {
            var genericArgs = string.Join(", ", type.GenericTypeArguments.Select(g => GetTypeReference(nullabilityInfo, currentType, g, false)));

            int backtickIndex = type.Name.IndexOf('`');
            string cleanName = backtickIndex >= 0 ? type.Name.Substring(0, backtickIndex) : type.Name;

            string nolink1 = $"{cleanName}&lt;{genericArgs}&gt;{append}";
            if (link) return $"[{nolink1}]({GetTypeReferenceCanonical(currentType, type)})";
            else return nolink1;
        }

        string nolink = $"{type.Name}{append}";
        if (link) return $"[{nolink}]({GetTypeReferenceCanonical(currentType, type)})";
        else return nolink;
    }

    public void GenerateForAssembly(string dllFilePath, string xmlFilePath, string assemblyName, string rootOutputDirectory)
    {
        Directory.CreateDirectory(rootOutputDirectory);

        DocXmlReader reader = new DocXmlReader(xmlFilePath);

        string dllDirectory = Path.GetDirectoryName(dllFilePath)!;
        var resolverPaths = new List<string>(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
        resolverPaths.AddRange(Directory.GetFiles(dllDirectory, "*.dll"));

        var resolver = new PathAssemblyResolver(resolverPaths);
        using var context = new MetadataLoadContext(resolver);

        Assembly assembly = context.LoadFromAssemblyPath(dllFilePath);

        BindingFlags declaredFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.DeclaredOnly;

        var namespaceGroups = new Dictionary<string, List<(Type Type, string Summary)>>();

        foreach (var type in assembly.ExportedTypes)
        {
            if (!type.IsVisible) continue;

            string className = type.Name;

            var xmlType = reader.GetTypeComments(type);
            string typeSummary = xmlType?.Summary?.Trim() ?? "";

            string nsKey = type.Namespace ?? string.Empty;
            if (!namespaceGroups.ContainsKey(nsKey)) namespaceGroups[nsKey] = new();
            namespaceGroups[nsKey].Add((type, typeSummary));

            StringBuilder fieldBuilder = new();
            StringBuilder propertyBuilder = new();
            StringBuilder methodBuilder = new();

            foreach (var field in type.GetFields(declaredFlags))
            {
                if (field.IsSpecialName) continue;
                bool isProtected = field.IsFamily || field.IsFamilyOrAssembly;
                if (field.IsPrivate || field.IsAssembly) continue;
                if (isProtected && type.IsSealed) continue;

                var xmlField = reader.GetMemberComment(field)?.Replace("\n", " ").Replace("<br>", " ").Replace("|", "\\|").Trim() ?? "";
                string modifiers = string.Empty;
                if (!type.IsEnum)
                {
                    modifiers = isProtected ? "protected " : "public ";
                    if (field.IsStatic) modifiers += "static ";
                    if (field.IsInitOnly) modifiers += "readonly ";
                }
                var nullabilityInfo = new NullabilityInfoContext().Create(field);
                fieldBuilder.AppendLine($"| `{modifiers}{field.Name}` | {GetTypeReference(nullabilityInfo, type, field.FieldType)} | {xmlField} |");
            }

            foreach (var property in type.GetProperties(declaredFlags))
            {
                if (property.IsSpecialName) continue;

                var getter = property.GetGetMethod(true);
                var setter = property.GetSetMethod(true);

                bool isGetterPublic = getter?.IsPublic == true;
                bool isSetterPublic = setter?.IsPublic == true;
                bool isGetterProtected = getter?.IsFamily == true || getter?.IsFamilyOrAssembly == true;
                bool isSetterProtected = setter?.IsFamily == true || setter?.IsFamilyOrAssembly == true;

                bool isPublic = isGetterPublic || isSetterPublic;
                bool isProtected = isGetterProtected || isSetterProtected;

                if (!isPublic && !isProtected) continue;
                if (!isPublic && isProtected && type.IsSealed) continue;
                var xmlProperty = reader.GetMemberComment(property)?.Replace("\n", " ").Replace("<br>", " ").Replace("|", "\\|").Trim() ?? "";

                string modifiers = string.Empty;
                if (isGetterPublic == isSetterPublic &&
                    ((getter == null) != (setter == null) || (getter?.IsStatic == setter?.IsStatic)))
                {
                    modifiers = isPublic ? "public " : "protected ";
                    if ((getter?.IsStatic ?? false) || (setter?.IsStatic ?? false)) modifiers += "static ";
                    if (getter != null) modifiers += "get; ";
                    if (setter != null) modifiers += "set; ";
                }
                else
                {
                    var AppendModifiers = (MethodInfo p) =>
                    {
                        modifiers += p.IsPublic ? "public " : "protected ";
                        if (p.IsStatic) modifiers += " static";
                    };
                    if (getter != null) { AppendModifiers(getter); modifiers += "get; "; }
                    if (setter != null) { AppendModifiers(setter); modifiers += "set; "; }
                }

                var nullabilityInfo = new NullabilityInfoContext().Create(property);
                propertyBuilder.AppendLine($"| `{modifiers}{property.Name}` | {GetTypeReference(nullabilityInfo, type, property.PropertyType)} | {xmlProperty} |");
            }

            foreach (var method in type.GetMethods(declaredFlags))
            {
                if (method.IsSpecialName) continue;

                bool isProtected = method.IsFamily || method.IsFamilyOrAssembly;
                if (method.IsPrivate || method.IsAssembly) continue;
                if (isProtected && type.IsSealed) continue;

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
                    $"{GetParamModifiers(p)}{GetTypeReference(new NullabilityInfoContext().Create(p), type, p.ParameterType)} {p.Name}");

                string joinedParams = string.Join(", ", paramSignatures);

                string methodGenArgs = string.Empty;
                if (method.IsGenericMethodDefinition)
                {
                    var genParams = method.GetGenericArguments().Select(t => t.Name);
                    methodGenArgs = $"&lt;{string.Join(", ", genParams)}&gt;";
                }

                string modifiers = isProtected ? "protected " : "public ";
                if (method.IsStatic) modifiers += "static ";
                else if (method.IsAbstract && !type.IsInterface) modifiers += "abstract ";
                else if (method.IsVirtual && !method.IsFinal && !type.IsInterface) modifiers += "virtual ";

                methodBuilder.AppendLine($"#### {modifiers}" +
                    $"{GetTypeReference(new NullabilityInfoContext().Create(method.ReturnParameter), type, method.ReturnType)} " +
                    $"{method.Name}{methodGenArgs}({joinedParams})");
                methodBuilder.AppendLine();

                string methodSummary = xmlMethod?.Summary?.Replace("\n", " ").Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(methodSummary))
                {
                    methodBuilder.AppendLine(methodSummary);
                    methodBuilder.AppendLine();
                }

                if (parameters.Length > 0)
                {
                    methodBuilder.AppendLine("**Parameters:**");
                    methodBuilder.AppendLine();
                    foreach (var p in parameters)
                    {
                        var xmlParam = xmlMethod?.Parameters?.FirstOrDefault(x => x.Name == p.Name);
                        string paramDescription = xmlParam?.Text?.Trim() ?? "";
                        methodBuilder.AppendLine($"- `{p.Name}` ({GetTypeReference(new NullabilityInfoContext().Create(p), type, p.ParameterType)}): {paramDescription}");
                        methodBuilder.AppendLine();
                    }
                    methodBuilder.AppendLine();
                }

                if (method.ReturnType.Name != "Void")
                {
                    methodBuilder.AppendLine("**Returns:**");
                    methodBuilder.AppendLine();
                    string returnDescription = xmlMethod?.Returns?.Trim() ?? "";
                    methodBuilder.AppendLine($"- {GetTypeReference(new NullabilityInfoContext().Create(method.ReturnParameter), type, method.ReturnType)}: {returnDescription}");
                    methodBuilder.AppendLine();
                }

                methodBuilder.AppendLine("---");
            }

            string typeKind = "???";
            if (type.IsEnum) typeKind = "enum";
            else if (type.IsInterface) typeKind = "interface";
            else if (type.IsClass) typeKind = "class";
            else if (type.IsValueType) typeKind = "struct";

            string typeDeclarationModifier = "";
            if (type.IsAbstract && type.IsSealed) typeDeclarationModifier = "static ";
            else
            {
                if (type.IsAbstract && !type.IsInterface) typeDeclarationModifier += "abstract ";
                if (type.IsSealed && !type.IsValueType) typeDeclarationModifier += "sealed ";
            }

            List<string> parents = new();
            Type? parent = type.BaseType;
            while (parent is not null)
            {
                parents.Add(GetTypeReference(null, type, parent));
                parent = parent.BaseType;
            }
            parents.Reverse();

            string inheritanceBuilder = parents.Count > 0
                ? string.Join(" ➔ ", parents) + " ➔ "
                : "";

            string interfaceBuilder = string.Join(", ", type.GetInterfaces().Select(i => GetTypeReference(null, type, i)));

            string template = $""""
                    # {type.Name}

                    {typeSummary}

                    ## Definition

                    **Namespace:** `{type.Namespace ?? "<Global>"}`  
                    **Assembly:** `{type.Assembly?.FullName?.Split(',')[0]}.dll`

                    ```csharp
                    {typeDeclarationModifier}{typeKind} {type.Name}
                    ```
                    {(type.IsEnum ? string.Empty :
                       ($""""
                       **Inheritance:**

                       ##### {inheritanceBuilder} **{type.Name}**

                       **Implements:**

                       ##### {interfaceBuilder}
                       """"
                       ))}
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

            string[] nsParts = GetNamespaceParts(type.Namespace ?? string.Empty);
            string finalDirectory = Path.Combine(rootOutputDirectory, string.Join("/", nsParts));
            Directory.CreateDirectory(finalDirectory);
            File.WriteAllText(Path.Combine(finalDirectory, $"{className.ToLower()}.md"), template);
        }

        StringBuilder indexBuilder = new StringBuilder();
        indexBuilder.AppendLine($"# {assemblyName}");
        indexBuilder.AppendLine();
        indexBuilder.AppendLine("## Namespaces");
        indexBuilder.AppendLine();

        string[] sourceNsParts = GetNamespaceParts(assemblyName); // The index.md lives at the root folder of the assembly

        foreach (var kvp in namespaceGroups.OrderBy(x => x.Key))
        {
            string nsName = string.IsNullOrEmpty(kvp.Key) ? "<Global>" : kvp.Key;
            indexBuilder.AppendLine($"### `{nsName}`");
            indexBuilder.AppendLine();
            indexBuilder.AppendLine("| Type | Description |");
            indexBuilder.AppendLine("| --- | --- |");

            foreach (var t in kvp.Value.OrderBy(x => x.Type.Name))
            {
                string cleanSummary = t.Summary.Replace("\n", " ").Replace("|", "\\|").Trim();

                string[] targetNsParts = GetNamespaceParts(t.Type.Namespace ?? string.Empty);

                // Calculate relative path from the root index.md down into the subfolders
                int commonCount = 0;
                while (commonCount < Math.Min(sourceNsParts.Length, targetNsParts.Length) && sourceNsParts[commonCount] == targetNsParts[commonCount])
                    commonCount++;

                string downPath = string.Join("/", targetNsParts.Skip(commonCount));
                if (!string.IsNullOrEmpty(downPath)) downPath += "/";

                string fileName = $"{t.Type.Name.ToLower()}.md";
                string linkPath = $"./{downPath}{fileName}";

                string displayName = t.Type.Name;
                if (t.Type.IsGenericType)
                {
                    int backtick = displayName.IndexOf('`');
                    if (backtick > 0) displayName = displayName.Substring(0, backtick);

                    var genArgs = string.Join(", ", t.Type.GetGenericArguments().Select(g => g.Name));
                    displayName = $"{displayName}&lt;{genArgs}&gt;";
                }

                indexBuilder.AppendLine($"| [{displayName}]({linkPath}) | {cleanSummary} |");
            }
            indexBuilder.AppendLine();
        }

        // SAVE THE INDEX FILE AT THE ROOT OF THE ASSEMBLY FOLDER
        string assemblyFolder = Path.Combine(rootOutputDirectory, assemblyName);
        Directory.CreateDirectory(assemblyFolder);
        File.WriteAllText(Path.Combine(assemblyFolder, "index.md"), indexBuilder.ToString());
    }
}