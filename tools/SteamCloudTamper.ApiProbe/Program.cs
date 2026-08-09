using System;
using System.Linq;
using System.Reflection;

var asm = typeof(SteamKit2.SteamClient).Assembly;

foreach (var tn in new[] { "SteamKit2.AsyncJob`1", "SteamKit2.CallbackManager", "SteamKit2.SteamConfiguration", "SteamKit2.SteamUnifiedMessages+ServiceMethodResponse`1", "SteamKit2.EResult" })
{
    var t = asm.GetType(tn);
    if (t == null) { Console.WriteLine($"MISSING TYPE {tn}"); continue; }
    Console.WriteLine($"== {t.FullName}");
    if (t.IsEnum)
    {
        Console.WriteLine("    " + string.Join(", ", Enum.GetNames(t)));
        continue;
    }
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    field {f.FieldType.Name} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (m.DeclaringType == typeof(object)) continue;
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "")));
        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({ps})");
    }
}

foreach (var tn in new[] { "SteamKit2.SteamUser", "SteamKit2.SteamUser+LogOnDetails", "SteamKit2.SteamAuthentication", "SteamKit2.SteamAuthentication+AuthSessionDetails" })
{
    var t = asm.GetType(tn);
    if (t == null) { Console.WriteLine($"MISSING TYPE {tn}"); continue; }
    Console.WriteLine($"== {t.FullName}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    field {f.FieldType.Name} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({ps})");
    }
}

foreach (var t in asm.GetTypes().Where(t => t.FullName!.Contains("Cloud", StringComparison.OrdinalIgnoreCase) || t.FullName!.Contains("UFS", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.FullName))
{
    Console.WriteLine($"== {t.FullName} ({t.Attributes})");
    if (t.IsInterface || t.IsClass)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.DeclaringType == typeof(object) || m.DeclaringType == typeof(SteamKit2.ClientMsgHandler)) continue;
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "")));
            Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({ps})");
        }
    }
    if (t.IsEnum)
    {
        Console.WriteLine("    " + string.Join(", ", Enum.GetNames(t)));
    }
}

var unified = asm.GetType("SteamKit2.SteamUnifiedMessages")!;
Console.WriteLine("== SteamUnifiedMessages");
foreach (var m in unified.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Static))
{
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({ps})");
}