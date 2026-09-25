using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ripcord.Core.Platform;

namespace Ripcord_App.Input;

/// <summary>
/// Writes down every focus move while it is switched on, so the question "what moved focus?" can be answered
/// with a log rather than a hypothesis.
///
/// <para>
/// <b>Why this exists.</b> Focus bugs in this app have a bad habit of looking like one cause and being
/// another, and the shell has six paths that seed focus plus a platform that moves it on its own. Two fixes
/// for the same symptom — a web-hosted sign-in box that deselects the instant it is clicked — were aimed at
/// the wrong mover, because a symptom seen by hand cannot tell you whose call stack it came from. The
/// decisive fact is in the stack: a move our code made has Ripcord frames in it, and a move the platform made
/// does not.
/// </para>
///
/// <para>
/// <b>Off unless asked for.</b> Set <c>RIPCORD_TRACE_FOCUS=1</c>. It takes a stack trace per focus event,
/// which is far too expensive to leave on, and a log of where the caret goes is not something to write to
/// disk on somebody's machine without being told to.
/// </para>
///
/// <para>
/// <b>It logs element identity and nothing else</b> — type and <c>x:Name</c>. Never <c>Text</c>, never a
/// <c>WebView2</c> source: this is switched on for a sign-in surface, so the one thing it must never do is
/// write down what is being typed into it.
/// </para>
/// </summary>
public sealed class FocusTrace : IDisposable
{
    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _running;

    private FocusTrace(string label, string path)
    {
        _path = path;

        Write($"==== focus trace: {label} — {DateTimeOffset.Now:O} ====");

        FocusManager.GettingFocus += OnGettingFocus;
        FocusManager.LosingFocus += OnLosingFocus;
        FocusManager.GotFocus += OnGotFocus;
        FocusManager.LostFocus += OnLostFocus;

        _running = true;
    }

    /// <summary>True when <c>RIPCORD_TRACE_FOCUS</c> asks for tracing.</summary>
    public static bool Enabled
        => Environment.GetEnvironmentVariable("RIPCORD_TRACE_FOCUS") is "1" or "true" or "TRUE";

    /// <summary>
    /// Start tracing, or return null when tracing is off — so a caller is one nullable field and a
    /// <c>?.Dispose()</c> rather than an <c>if</c> at both ends.
    /// </summary>
    public static FocusTrace? StartIfEnabled(string label)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            string path = Path.Combine(new DefaultPlatformPaths().StateDirectory, "focus-trace.log");
            return new FocusTrace(label, path);
        }
        catch (Exception ex)
        {
            // A diagnostic that can break the surface it is diagnosing is worse than no diagnostic.
            Debug.WriteLine($"[Ripcord] focus trace could not start: {ex}");
            return null;
        }
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        FocusManager.GettingFocus -= OnGettingFocus;
        FocusManager.LosingFocus -= OnLosingFocus;
        FocusManager.GotFocus -= OnGotFocus;
        FocusManager.LostFocus -= OnLostFocus;

        Write("==== focus trace ends ====");
    }

    /// <summary>
    /// <c>type#name</c>, or what can be had of it. Deliberately not the element's content — see the class
    /// note. A null element is written as <c>none</c>, which for a <c>WebView2</c> click is the whole point:
    /// XAML focus genuinely goes nowhere, because the thing with focus is a native child window.
    /// </summary>
    private static string Describe(object? element)
    {
        if (element is null)
        {
            return "none";
        }

        string type = element.GetType().Name;

        return element is FrameworkElement { Name.Length: > 0 } named
            ? $"{type}#{named.Name}"
            : type;
    }

    /// <summary>
    /// The frames that answer the question. Ripcord frames are kept in full, because one of them is the
    /// culprit whenever the culprit is ours; the rest are counted rather than listed, since a wall of WinUI
    /// dispatch frames says only "the platform", which is exactly as much as it needs to say.
    ///
    /// <para>
    /// <b>The entry point does not count as blame, and the first version of this got that wrong.</b> Every
    /// frame in the process sits under <c>Program.Main</c>, so counting it made even a pure platform-dispatched
    /// mouse click read as ours — which would have hidden the one answer this method exists to give. A move
    /// arriving from the message loop has nothing of ours between it and <c>Main</c>, and that is the
    /// signature to look for.
    /// </para>
    /// </summary>
    private static string Blame()
    {
        var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
        List<string> ours = [];
        int others = 0;

        foreach (StackFrame frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            string? owner = method?.DeclaringType?.FullName;

            if (owner is null)
            {
                continue;
            }

            if (!owner.StartsWith("Ripcord", StringComparison.Ordinal))
            {
                others++;
                continue;
            }

            // The process entry point and the generated application bootstrap are under everything, so they
            // say nothing about who moved focus.
            if (method!.Name is "Main" or "InvokeMain" or "OnLaunched")
            {
                continue;
            }

            ours.Add($"{owner.Split('.')[^1]}.{method.Name}");
        }

        return ours.Count == 0
            ? $"PLATFORM ONLY ({others} non-Ripcord frames)"
            : string.Join(" <- ", ours) + $" (+{others})";
    }

    private void OnGettingFocus(object? sender, GettingFocusEventArgs e)
        => Write($"GettingFocus  {Describe(e.OldFocusedElement)} -> {Describe(e.NewFocusedElement)}  "
                 + $"device={e.InputDevice} dir={e.Direction} state={e.FocusState} cancel={e.Cancel}  {Blame()}");

    private void OnLosingFocus(object? sender, LosingFocusEventArgs e)
        => Write($"LosingFocus   {Describe(e.OldFocusedElement)} -> {Describe(e.NewFocusedElement)}  "
                 + $"device={e.InputDevice} dir={e.Direction} state={e.FocusState} cancel={e.Cancel}  {Blame()}");

    private void OnGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
        => Write($"GotFocus      {Describe(e.NewFocusedElement)}");

    private void OnLostFocus(object? sender, FocusManagerLostFocusEventArgs e)
        => Write($"LostFocus     {Describe(e.OldFocusedElement)}");

    private void Write(string line)
    {
        try
        {
            File.AppendAllText(_path, $"[{_clock.ElapsedMilliseconds,7} ms] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception)
        {
            // Same rule as the crash log: diagnostics never take the app down.
        }
    }
}
