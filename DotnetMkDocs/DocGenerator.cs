using LoxSmoke.DocXml;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetMkDocs;

public class DocGenerator
{
    public class NavNode
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, NavNode> Folders { get; } = new();
        public List<(string Title, string Path)> Pages { get; } = new();
    }

    private readonly NavNode _rootNav = new NavNode { Name = "API Reference" };

    private string[] GetNamespaceParts(string owningAssemblyName, string ns, bool tolower)
    {
        if (string.IsNullOrEmpty(ns)) return Array.Empty<string>();

        string[] rawParts = ns.Split('.');

        if (!string.IsNullOrEmpty(owningAssemblyName) && ns.StartsWith(owningAssemblyName))
        {
            var parts = new List<string> { owningAssemblyName };

            string remainder = ns.Substring(owningAssemblyName.Length).TrimStart('.');
            if (!string.IsNullOrEmpty(remainder))
            {
                parts.AddRange(remainder.Split('.'));
            }

            return (tolower ? parts.Select(s => s.ToLower()) : parts).ToArray();
        }

        // Fallback for completely external namespaces
        return (tolower ? ns.ToLower() : ns).Split('.');
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

        string sourceAsm = currentType.Assembly.GetName().Name ?? string.Empty;
        string targetAsm = targetType.Assembly.GetName().Name ?? string.Empty;
        string[] sourceParts = GetNamespaceParts(sourceAsm, currentNs, true);
        string[] targetParts = GetNamespaceParts(targetAsm, targetNs, true);

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
            if (link && !elementType.IsGenericParameter)
                return $"[{nolink0}]({GetTypeReferenceCanonical(currentType, elementType)})";
            else
                return nolink0;
        }
        else if (type.IsGenericTypeDefinition || type.IsGenericType)
        {
            var genericArgs = string.Join(", ",
                type.GenericTypeArguments.Select(g => GetTypeReference(nullabilityInfo, currentType, g, false)));

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

    private string ResolveInlineTags_SeeCref(string xmlText, Type? currentType, MetadataLoadContext context)
    {
        if (string.IsNullOrEmpty(xmlText)) return string.Empty;

        // Matches <see cref="X:Namespace.Type" /> or <see cref="X:Namespace.Type"/>
        // Captures the prefix and the full path
        string pattern = @"<see\s+cref=""([A-Z]):([^""]+)""\s*/?>";

        return Regex.Replace(xmlText, pattern, match =>
        {
            string memberType = match.Groups[1].Value; // T, M, P, F
            string fullPath = match.Groups[2].Value;

            // Handle Types (T:)
            if (memberType == "T")
            {
                if (currentType != null)
                {
                    Type? targetType = context.GetAssemblies()
                        .Select(a => a.GetType(fullPath))
                        .FirstOrDefault(t => t != null);

                    if (targetType != null)
                    {
                        return GetTypeReference(null, currentType, targetType);
                    }
                }
                // Fallback if type isn't loaded/found (just show name)
                return $"`{fullPath.Split('.').Last()}`";
            }

            // Handle Members (M: Methods, P: Properties, F: Fields
            int methodSignatureCutoff = fullPath.IndexOf('(');// method signature
            string memberSignature = methodSignatureCutoff == -1 ? fullPath : fullPath.Substring(0, methodSignatureCutoff);
            if (!string.IsNullOrEmpty(memberSignature))
            {
                int pivot = memberSignature.LastIndexOf('.');
                string typePath = memberSignature.Substring(0, pivot);
                string memberName = memberSignature.Substring(pivot + 1);

                if (currentType != null)
                {
                    Type? targetType = context.GetAssemblies()
                    .Select(a => a.GetType(typePath))
                    .FirstOrDefault(t => t != null);

                    if (targetType != null)
                    {
                        string link = GetTypeReferenceCanonical(currentType, targetType);
                        // Links directly to the type file, hitting the Markdown anchor tag for the member
                        return
                            $"[{GetTypeReference(null, currentType, targetType, false)}.{memberName}]({link}#{memberName.ToLower()})";
                    }
                }

                // fallback
                return $"`{memberName}`";
            }

            return match.Value; // Fallback to original text if nothing matches well
        });
    }
    private string ResolveInlineTags_Paramref(string xmlText)
    {
        // Matches <paramref name="myParam" /> or <paramref name="myParam"></paramref>
        string pattern = @"<paramref\s+name=""([^""]+)""\s*(?:/>|></paramref>)";

        return Regex.Replace(xmlText, pattern, match =>
        {
            string paramName = match.Groups[1].Value;
            return $"`{paramName}`";
        });
    }
    public string ResolveInlineTags(string xmlText, Type? currentType, MetadataLoadContext context)
    {
        if (string.IsNullOrEmpty(xmlText)) return string.Empty;
        xmlText = ResolveInlineTags_SeeCref(xmlText, currentType, context);
        xmlText = ResolveInlineTags_Paramref(xmlText);
        return xmlText;
    }

    public void GenerateForAssembly(string dllFilePath, string xmlFilePath, string assemblyName,
        string rootOutputDirectory)
    {
        Directory.CreateDirectory(rootOutputDirectory);

        DocXmlReader reader = new DocXmlReader(xmlFilePath);

        string dllDirectory = Path.GetDirectoryName(dllFilePath)!;
        var resolverPaths =
            new List<string>(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                "*.dll"));
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
            string? typeRemarks = xmlType?.Remarks?.Trim();

            string nsKey = type.Namespace ?? string.Empty;
            if (!namespaceGroups.ContainsKey(nsKey)) namespaceGroups[nsKey] = new();
            namespaceGroups[nsKey].Add((type, typeSummary));

            StringBuilder fieldBuilder = new();
            StringBuilder propertyBuilder = new();
            StringBuilder methodBuilder = new();
            StringBuilder fieldRemarksBuilder = new();
            StringBuilder propertyRemarksBuilder = new();

            foreach (var field in type.GetFields(declaredFlags))
            {
                if (field.IsSpecialName) continue;
                bool isProtected = field.IsFamily || field.IsFamilyOrAssembly;
                if (field.IsPrivate || field.IsAssembly) continue;
                if (isProtected && type.IsSealed) continue;

                string modifiers = string.Empty;
                if (!type.IsEnum)
                {
                    modifiers = isProtected ? "protected " : "public ";
                    if (field.IsStatic) modifiers += "static ";
                    if (field.IsInitOnly) modifiers += "readonly ";
                }

                var xmlField = reader.GetMemberComments(field);
                string summary = xmlField?.Summary?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";
                string? remarks = xmlField?.Remarks?.Trim();
                var nullabilityInfo = new NullabilityInfoContext().Create(field);
                fieldBuilder.AppendLine(
                    $"| `{modifiers}{field.Name}` | {GetTypeReference(nullabilityInfo, type, field.FieldType)} | {ResolveInlineTags(summary, type, context)} |");

                if (!string.IsNullOrEmpty(remarks))
                {
                    fieldRemarksBuilder.AppendLine(
                        $"##### `{field.Name}` Remarks\n{ResolveInlineTags(remarks, type, context)}\n");
                }
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
                        if (p.IsStatic) modifiers += "static ";
                    };
                    if (getter != null)
                    {
                        AppendModifiers(getter);
                        modifiers += "get; ";
                    }

                    if (setter != null)
                    {
                        AppendModifiers(setter);
                        modifiers += "set; ";
                    }
                }

                var xmlProperty = reader.GetMemberComments(property);
                string summary = xmlProperty?.Summary?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";
                string? remarks = xmlProperty?.Remarks?.Trim();

                var nullabilityInfo = new NullabilityInfoContext().Create(property);

                propertyBuilder.AppendLine(
                    $"| `{modifiers}{property.Name}` | {GetTypeReference(nullabilityInfo, type, property.PropertyType)} | {ResolveInlineTags(summary, type, context)} |");

                if (!string.IsNullOrEmpty(remarks))
                {
                    propertyRemarksBuilder.AppendLine(
                        $"##### `{property.Name}` Remarks\n{ResolveInlineTags(remarks, type, context)}\n");
                }
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

                string? methodSummary = xmlMethod?.Summary;
                if (!string.IsNullOrWhiteSpace(methodSummary))
                {
                    methodBuilder.AppendLine();
                    methodBuilder.AppendLine("**Summary:**");
                    methodBuilder.AppendLine(ResolveInlineTags(methodSummary, type, context));
                    methodBuilder.AppendLine();
                }

                string? methodRemarks = xmlMethod?.Remarks;
                if (!string.IsNullOrWhiteSpace(methodRemarks))
                {
                    methodBuilder.AppendLine("**Remarks:**");
                    methodBuilder.AppendLine(ResolveInlineTags(methodRemarks, type, context));
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
                        methodBuilder.AppendLine(
                            $"- `{p.Name}` ({GetTypeReference(new NullabilityInfoContext().Create(p), type, p.ParameterType)}): {ResolveInlineTags(paramDescription, type, context)}");
                        methodBuilder.AppendLine();
                    }

                    methodBuilder.AppendLine();
                }

                if (method.ReturnType.Name != "Void")
                {
                    methodBuilder.AppendLine("**Returns:**");
                    methodBuilder.AppendLine();
                    string returnDescription = xmlMethod?.Returns?.Trim() ?? "";
                    methodBuilder.AppendLine(
                        $"- {GetTypeReference(new NullabilityInfoContext().Create(method.ReturnParameter), type, method.ReturnType)}: {ResolveInlineTags(returnDescription, type, context)}");
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

            string interfaceBuilder =
                string.Join(", ", type.GetInterfaces().Select(i => GetTypeReference(null, type, i)));

            string typeNamePretty = GetTypeReference(null, type, type, false);

            string template = $""""
                               # {typeNamePretty}

                               ## Summary
                               {ResolveInlineTags(typeSummary, type, context)}

                               {(!string.IsNullOrWhiteSpace(typeRemarks) ? $"## Remarks\n{ResolveInlineTags(typeRemarks!, type, context)}" : string.Empty)}

                               ## Definition

                               **Namespace:** `{type.Namespace ?? "<Global>"}`  
                               **Assembly:** `{type.Assembly?.FullName?.Split(',')[0]}.dll`

                               ```csharp
                               {typeDeclarationModifier}{typeKind} {typeNamePretty.Replace("&lt;", "<").Replace("&gt;", ">")}
                               ```
                               {(type.IsEnum ? string.Empty :
                                   (((type.IsValueType || type.IsInterface) ? string.Empty : $""""
                                         **Inheritance:**

                                         ##### {inheritanceBuilder} **{typeNamePretty}**

                                         """") +
                                    $""""
                                     **Implements:**

                                     ##### {interfaceBuilder}
                                     """"
                                   ))}
                               ---

                               ## Fields

                               | Name | Type | Description |
                               | --- | --- | --- |
                               {fieldBuilder}

                               {fieldRemarksBuilder}
                               ---
                               
                               ## Properties
                               
                               | Name | Type | Description |
                               | --- | --- | --- |
                               {propertyBuilder}

                               {propertyRemarksBuilder}
                               ---

                               ## Methods

                               {methodBuilder}

                               ---
                               """";

            string[] nsParts = GetNamespaceParts(assemblyName, type.Namespace ?? string.Empty, true);
            string finalDirectory = Path.Combine(rootOutputDirectory, string.Join("/", nsParts));
            Directory.CreateDirectory(finalDirectory);
            File.WriteAllText(Path.Combine(finalDirectory, $"{className.ToLower()}.md"), template);
        }

        StringBuilder indexBuilder = new StringBuilder();
        indexBuilder.AppendLine($"# {assemblyName}");
        indexBuilder.AppendLine();
        indexBuilder.AppendLine("## Namespaces");
        indexBuilder.AppendLine();

        string[]
            sourceNsParts =
                GetNamespaceParts(assemblyName, assemblyName,
                    true); // The index.md lives at the root folder of the assembly

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
                cleanSummary = ResolveInlineTags(cleanSummary, null, context);

                string[] targetNsParts = GetNamespaceParts(assemblyName, t.Type.Namespace ?? string.Empty, true);

                // Calculate relative path from the root index.md down into the subfolders
                int commonCount = 0;
                while (commonCount < Math.Min(sourceNsParts.Length, targetNsParts.Length) &&
                       sourceNsParts[commonCount] == targetNsParts[commonCount])
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
        string assemblyFolder = Path.Combine(rootOutputDirectory, assemblyName.ToLower());
        Directory.CreateDirectory(assemblyFolder);
        File.WriteAllText(Path.Combine(assemblyFolder, "index.md"), indexBuilder.ToString());

        string[] asmParts = GetNamespaceParts(assemblyName, assemblyName, false);
        NavNode asmNode = _rootNav;
        foreach (var part in asmParts)
        {
            if (!asmNode.Folders.ContainsKey(part)) asmNode.Folders[part] = new NavNode { Name = part };
            asmNode = asmNode.Folders[part];
        }

        // Add the assembly root index
        asmNode.Pages.Add(("Overview", $"{assemblyName.ToLower()}/index.md"));

        // Add all the types into their respective folders
        foreach (var kvp in namespaceGroups)
        {
            foreach (var t in kvp.Value)
            {
                string[] parts = GetNamespaceParts(t.Type.Assembly.GetName().Name ?? string.Empty,
                    t.Type.Namespace ?? string.Empty, false);
                NavNode node = _rootNav;
                foreach (var part in parts)
                {
                    if (!node.Folders.ContainsKey(part)) node.Folders[part] = new NavNode { Name = part };
                    node = node.Folders[part];
                }

                string filePath = string.Join("/", parts) + $"/{t.Type.Name.ToLower()}.md";

                string displayName = t.Type.Name;
                if (t.Type.IsGenericType)
                {
                    int backtick = displayName.IndexOf('`');
                    if (backtick > 0) displayName = displayName.Substring(0, backtick);
                    var genArgs = string.Join(", ", t.Type.GetGenericArguments().Select(g => g.Name));
                    displayName = $"{displayName}<{genArgs}>";
                }

                node.Pages.Add((displayName, filePath));
            }
        }
    }

    public void ExportNavYaml(string outputPath, string prefixPath = "api-reference/")
    {
        StringBuilder sb = new StringBuilder();

        // Recursively build the YAML
        void WriteNode(NavNode node, int indentLevel)
        {
            string indent = new string(' ', indentLevel * 2);

            // Put 'Overview' index.md at the top, then sort the rest alphabetically
            foreach (var page in node.Pages.OrderBy(x => x.Title == "Overview" ? 0 : 1).ThenBy(x => x.Title))
            {
                sb.AppendLine(
                    $"{indent}- \"{page.Title.Replace("<", "&lt;").Replace(">", "&gt;")}\": {prefixPath.ToLower()}{page.Path.ToLower()}");
            }

            foreach (var folder in node.Folders.OrderBy(x => x.Key))
            {
                sb.AppendLine($"{indent}- {folder.Key}:");
                WriteNode(folder.Value, indentLevel + 1);
            }
        }

        WriteNode(_rootNav, 0); // Start with 0 indent so you can easily copy-paste it
        File.WriteAllText(outputPath, sb.ToString());
    }
}