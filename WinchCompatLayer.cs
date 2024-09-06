using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Logger = BepInEx.Logging.Logger;

namespace WinchCompatLayer;

// ReSharper disable once UnusedType.Global
internal static class WinchCompatLayer
{
    private const string Id = "com.grahamkracker.abyss.winchcompatlayer";
    private const string Name = "WinchCompatLayer";

    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<string> TargetDLLs { get; } =
        Array.Empty<string>(); // Needed in order to get recognized as a patcher

    private static ManualLogSource _logger = null!;
    private static ManualLogSource _winchLogger = null!;
    private static Harmony _harmonyInstance = null!;

    public static void Patch(AssemblyDefinition assemblyDefinition)
    {
        // Needed in order to get recognized as a patcher
    }


// ReSharper disable once UnusedMember.Global
    public static void Finish() //called by bie
    {
        AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;
        _logger = Logger.CreateLogSource(Name);
        _winchLogger = Logger.CreateLogSource("Winch");
        _harmonyInstance = new Harmony(Id);
        _harmonyInstance.Patch(typeof(Chainloader).GetMethod(nameof(Chainloader.Initialize)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(WinchCompatLayer), nameof(InitWinch))));
    }

    private static string WinchPath => Path.Combine(Paths.GameRootPath, "Winch");

private static void DownloadWinch()
    {
        var winchUrl = "https://github.com/DREDGE-Mods/Winch/releases/latest/download/Winch.zip";

        if (File.Exists(Path.Combine(WinchPath, "Winch.dll")))
            return;

        try
        {
            using var webClient = new WebClient();
            if (Directory.Exists(WinchPath))
                Directory.Delete(WinchPath, true);
            Directory.CreateDirectory(WinchPath);
            webClient.DownloadFile(winchUrl, Path.Combine(WinchPath, "Winch.zip"));
            System.IO.Compression.ZipFile.ExtractToDirectory(Path.Combine(WinchPath, "Winch.zip"), WinchPath);
            File.Delete(Path.Combine(WinchPath, "Winch.zip"));
            var releaseDir = Path.Combine(WinchPath, "Release");

            //move all files and folders from Release to Winch
            foreach (var file in Directory.GetFiles(releaseDir))
            {
                File.Move(file, Path.Combine(WinchPath, Path.GetFileName(file)));
            }
            foreach (var dir in Directory.GetDirectories(releaseDir))
            {
                Directory.Move(dir, Path.Combine(WinchPath, Path.GetFileName(dir)));
            }

            Directory.Delete(releaseDir);
        }
        catch (Exception e)
        {
            _logger.LogWarning($"Failed to download winch.zip from {winchUrl}.");

            if (e is WebException webException)
            {
                _logger.LogError(webException.Message);
                _logger.LogError(webException.StackTrace);
            }
            else
            {
                _logger.LogError(e);
            }
        }
    }

    private static void InitWinch()
    {
        DownloadWinch();

        var winchAsm = Assembly.LoadFile(Path.Combine(Paths.GameRootPath, "Winch", "Winch.dll"));
        var logLevelType = winchAsm.GetType("Winch.Logging.Logger").GetField("_minLogLevel", BindingFlags.NonPublic | BindingFlags.Instance).FieldType.GenericTypeArguments[0];
        _harmonyInstance.Patch(AccessTools.Method(winchAsm.GetType("Winch.Logging.Logger"), "Log", [
            logLevelType, typeof(string), typeof(string)
        ]), postfix: new HarmonyMethod(typeof(WinchCompatLayer).GetMethod(nameof(WinchMessageLogged))));
        winchAsm.GetType("Winch.Core.WinchCore").GetMethod("Main")!.Invoke(null, null);

    }

    public static void WinchMessageLogged(dynamic level, string message, string source)
    {
        var logMessage = $"[{level}] : {message}";
        switch ((int) level)
        {
            case 2 /*Winch.LogLevel.INFO*/:
                _winchLogger.LogInfo(logMessage);
                break;
            case 1 /*Winch.LogLevel.DEBUG*/:
                _winchLogger.LogInfo(logMessage);
                break;
            case 3 /*Winch.LogLevel.WARN*/:
                _winchLogger.LogWarning(logMessage);
                break;
            case 4 /*Winch.LogLevel.ERROR*/:
                _winchLogger.LogError(logMessage);
                break;
            case 0 /*Winch.LogLevel.UNITY*/:
                _winchLogger.LogInfo(logMessage);
                break;
            default:
                _winchLogger.LogMessage(logMessage);
                break;
        }
    }

    private static Assembly LocalResolve(object sender, ResolveEventArgs args)
    {
        if (!Utility.TryParseAssemblyName(args.Name, out var assemblyName))
            return null;

        // Use parse assembly name on managed side because native GetName() can fail on some locales
        // if the game path has "exotic" characters
        var validAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(a => new
                { assembly = a, name = Utility.TryParseAssemblyName(a.FullName, out var name) ? name : null })
            .Where(a => a.name != null && a.name.Name == assemblyName.Name)
            .OrderByDescending(a => a.name.Version)
            .ToList();

        // First try to match by version, then just pick the best match (generally highest)
        // This should mainly affect cases where the game itself loads some assembly (like Mono.Cecil)
        var foundMatch = validAssemblies.Find(a => a.name.Version == assemblyName.Version) ??
                         validAssemblies.FirstOrDefault();
        var foundAssembly = foundMatch?.assembly;

        if (foundAssembly != null)
            return foundAssembly;

        if (Utility.TryResolveDllAssembly(assemblyName, WinchPath, out foundAssembly)
            || Utility.TryResolveDllAssembly(assemblyName, Path.Combine(Paths.GameRootPath, "Mods"), out foundAssembly))
            return foundAssembly;

        return null;
    }
}