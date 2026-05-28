using DocXml.Reflection;
using LoxSmoke.DocXml;
using System.ComponentModel.Design;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace DotnetMkDocs;

public class DocGenerator
{
    string GetTypeReferenceCanonical(Type currentType, Type targetType)
    {
        string currentNs = currentType.Namespace ?? string.Empty;
        string targetNs = targetType.Namespace ?? string.Empty;

        // Intercept System and Microsoft types and point to official Docs
        if (targetNs.StartsWith("System") || targetNs.StartsWith("Microsoft"))
        {
            // Microsoft routes generics using dashes (e.g. List`1 becomes list-1)
            string cleanName = targetType.Name.Replace('`', '-').ToLower();
            return $"https://learn.microsoft.com/dotnet/api/{targetNs.ToLower()}.{cleanName}";
        }

        // If they are in the exact same namespace, link directly to the file
        if (currentNs == targetNs)
            return $"{targetType.Name.ToLower()}.md";

        // Calculate relative path for your own cross-module classes
        string[] sourceParts = string.IsNullOrEmpty(currentNs) ? Array.Empty<string>() : currentNs.ToLower().Split('.');
        string[] targetParts = string.IsNullOrEmpty(targetNs) ? Array.Empty<string>() : targetNs.ToLower().Split('.');

        int commonCount = 0;
        int minLength = Math.Min(sourceParts.Length, targetParts.Length);

        while (commonCount < minLength && sourceParts[commonCount] == targetParts[commonCount])
        {
            commonCount++;
        }

        int dirsUp = sourceParts.Length - commonCount;
        string upPath = string.Concat(Enumerable.Repeat("../", dirsUp));

        string downPath = string.Join("/", targetParts.Skip(commonCount));
        if (!string.IsNullOrEmpty(downPath))
        {
            downPath += "/";
        }

        return $"{upPath}{downPath}{targetType.Name.ToLower()}.md";
    }

    string GetTypeReference(Type currentType, Type type, bool link = true)
    {
        if (type.IsGenericParameter) return type.Name;
        var GetElementName = (Type t) => $"{t.Name}{(Nullable.GetUnderlyingType(t) != null ? "?" : "")}";
        if (type.IsByRef)
        {
            return GetTypeReference(currentType, type.GetElementType()!);
        }
        else if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            string nolink0 = $"{GetElementName(elementType)}[]";
            if (link) return $"[{nolink0}]({GetTypeReferenceCanonical(currentType, elementType)})";
            else return nolink0;
        }
        else if (type.IsGenericTypeDefinition || type.IsGenericType)
        {
            var genericArgs = string.Join(", ", type.GenericTypeArguments.Select(g => GetTypeReference(currentType, g, false)));

            // Safety check: ensure the backtick actually exists before substringing
            int backtickIndex = type.Name.IndexOf('`');
            string cleanName = backtickIndex >= 0 ? type.Name.Substring(0, backtickIndex) : type.Name;

            string nolink1 = $"{cleanName}&lt;{genericArgs}&gt;";
            if (link) return $"[{nolink1}]({GetTypeReferenceCanonical(currentType, type)})";
            else return nolink1;
        }
        string nolink = $"{GetElementName(type)}";
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

        foreach (var type in assembly.ExportedTypes)
        {
            if (!type.IsVisible) continue;

            string className = type.Name;

            // Null safety for type comments
            var xmlType = reader.GetTypeComments(type);
            string typeSummary = xmlType?.Summary?.Trim() ?? "";

            StringBuilder fieldBuilder = new();
            StringBuilder propertyBuilder = new();
            StringBuilder methodBuilder = new();

            foreach (var field in type.GetFields(declaredFlags))
            {
                if (field.IsSpecialName) continue;
                bool isProtected = field.IsFamily || field.IsFamilyOrAssembly;
                if (field.IsPrivate || field.IsAssembly) continue;
                if (isProtected && type.IsSealed) continue;

                var xmlField = reader.GetMemberComment(field)?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";
                string modifiers = string.Empty;
                if (!type.IsEnum)
                {
                    modifiers = isProtected ? "protected " : "public ";
                    if (field.IsStatic) modifiers += "static ";
                    if (field.IsInitOnly) modifiers += "readonly ";
                }
                fieldBuilder.AppendLine($"| `{modifiers}{field.Name}` | {GetTypeReference(type, field.FieldType)} | {xmlField} |");
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

                // Visibility filtering
                if (!isPublic && !isProtected) continue; // Hide private/internal
                if (!isPublic && isProtected && type.IsSealed) continue; // Hide protected if sealed

                var xmlProperty = reader.GetMemberComment(property)?.Replace("\n", " ").Replace("|", "\\|").Trim() ?? "";

                string modifiers = string.Empty;
                if (isGetterPublic == isSetterPublic &&
                    ((getter == null) != (setter == null) || (getter?.IsStatic == setter?.IsStatic)))
                {
                    modifiers = isPublic ? "public " : "protected ";
                    if ((getter?.IsStatic ?? false) || (setter?.IsStatic ?? false)) modifiers += "static ";
                    if (getter != null) modifiers += "get; ";
                    if (setter != null) modifiers += "set; ";
                }
                else {
                    var AppendModifiers = (MethodInfo p) =>
                    {
                        modifiers += p.IsPublic ? "public " : "protected ";
                        if (p.IsStatic) modifiers += " static";
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

                propertyBuilder.AppendLine($"| `{modifiers}{property.Name}` | {GetTypeReference(type, property.PropertyType)} | {xmlProperty} |");
            }

            foreach (var method in type.GetMethods(declaredFlags))
            {
                if (method.IsSpecialName) continue;

                // Visibility filtering
                bool isProtected = method.IsFamily || method.IsFamilyOrAssembly;
                if (method.IsPrivate || method.IsAssembly) continue; // Hide private/internal
                if (isProtected && type.IsSealed) continue;        // Hide protected if sealed

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

                methodBuilder.AppendLine($"#### {modifiers}{GetTypeReference(type, method.ReturnType)} {method.Name}{methodGenArgs}({joinedParams})");
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
                        // Null safety for parameter comments
                        var xmlParam = xmlMethod?.Parameters?.FirstOrDefault(x => x.Name == p.Name);
                        string paramDescription = xmlParam?.Text?.Trim() ?? "";

                        methodBuilder.AppendLine($"- `{p.Name}` ({GetTypeReference(type, p.ParameterType)}): {paramDescription}");
                        methodBuilder.AppendLine();
                    }
                    methodBuilder.AppendLine();
                }

                if (method.ReturnType.Name != "Void")
                {
                    methodBuilder.AppendLine("**Returns:**");
                    methodBuilder.AppendLine();

                    // Null safety for return comments
                    string returnDescription = xmlMethod?.Returns?.Trim() ?? "";

                    methodBuilder.AppendLine($"- {GetTypeReference(type, method.ReturnType)}: {returnDescription}");
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

            string relativeNamespacePath = type.Namespace?.Replace('.', '/') ?? string.Empty;
            string finalDirectory = Path.Combine(rootOutputDirectory, relativeNamespacePath.ToLower());
            Directory.CreateDirectory(finalDirectory);
            File.WriteAllText(Path.Combine(finalDirectory, $"{className.ToLower()}.md"), template);
        }
    }
}