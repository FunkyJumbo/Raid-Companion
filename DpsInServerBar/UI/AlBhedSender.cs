using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace DpsInServerBar.UI;

/// <summary>
/// Easter egg, opt-in: with the plugin in Al Bhed and the option on, what you type in the chat box is sent in Al Bhed,
/// so the other players read it ciphered. Only chat goes through: plain text (current channel) and the chat channel
/// commands (/p, /s, /tell...). Any other command, the placeholders (&lt;t&gt;...), item links and auto-translate
/// phrases are left as they are.
/// </summary>
public sealed unsafe class AlBhedSender : IDisposable
{
    private readonly IPluginLog log;
    private readonly Hook<ShellCommandModule.Delegates.ExecuteCommandInner>? hook;

    // Chat channel commands: the text after them is ciphered. Tells also name who they go to, kept as it is.
    private static readonly HashSet<string> ChannelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/s", "/say", "/sh", "/shout", "/y", "/yell", "/p", "/party", "/a", "/alliance", "/fc", "/freecompany",
        "/n", "/novice", "/r", "/reply", "/pvpteam", "/pt",
        "/l", "/linkshell", "/l1", "/l2", "/l3", "/l4", "/l5", "/l6", "/l7", "/l8",
        "/linkshell1", "/linkshell2", "/linkshell3", "/linkshell4", "/linkshell5", "/linkshell6", "/linkshell7", "/linkshell8",
        "/cwl1", "/cwl2", "/cwl3", "/cwl4", "/cwl5", "/cwl6", "/cwl7", "/cwl8",
        "/cwlinkshell1", "/cwlinkshell2", "/cwlinkshell3", "/cwlinkshell4", "/cwlinkshell5", "/cwlinkshell6",
        "/cwlinkshell7", "/cwlinkshell8",
    };

    private static readonly HashSet<string> TellCommands = new(StringComparer.OrdinalIgnoreCase) { "/t", "/tell" };

    public AlBhedSender(IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;
        try
        {
            hook = interop.HookFromAddress<ShellCommandModule.Delegates.ExecuteCommandInner>(
                ShellCommandModule.Addresses.ExecuteCommandInner.Value, OnExecuteCommand);
        }
        catch (Exception e)
        {
            log.Error(e, "Al Bhed chat: could not hook the chat box");
        }
    }

    /// <summary>On only while the plugin is in Al Bhed and the option is ticked.</summary>
    public bool Enabled
    {
        get => hook?.IsEnabled == true;
        set
        {
            if (hook is null || hook.IsEnabled == value)
                return;
            if (value)
                hook.Enable();
            else
                hook.Disable();
        }
    }

    public void Dispose() => hook?.Dispose();

    private void OnExecuteCommand(ShellCommandModule* module, Utf8String* message, UIModule* uiModule)
    {
        try
        {
            if (Loc.Code == "al" && message != null && Cipher(message->AsSpan()) is { } ciphered)
                message->SetString(ciphered);
        }
        catch (Exception e)
        {
            // Never block the message: on any trouble it goes out as typed.
            log.Warning($"Al Bhed chat: {e.Message}");
        }

        hook!.Original(module, message, uiModule);
    }

    /// <summary>The line with its chat text in Al Bhed, or null when it is not chat (another command) or has no text.</summary>
    private static byte[]? Cipher(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
            return null;

        var line = SeString.Parse(raw);
        var text = line.Payloads.OfType<TextPayload>().FirstOrDefault()?.Text ?? string.Empty;

        // How much of the start of the first text is the command (and the tell's recipient), kept as typed.
        var keep = 0;
        if (line.Payloads.FirstOrDefault() is TextPayload && text.StartsWith('/'))
        {
            var words = text.Split(' ');
            var command = words[0];
            if (ChannelCommands.Contains(command))
            {
                keep = command.Length;
            }
            else if (TellCommands.Contains(command) && words.Length > 1)
            {
                // "/t <t> text", "/t First Last@World text".
                var recipientWords = words[1].StartsWith('<') ? 1 : 2;
                keep = string.Join(' ', words.Take(1 + recipientWords)).Length;
            }
            else
            {
                return null; // any other command: untouched
            }
        }

        var changed = false;
        var result = new SeStringBuilder();
        var first = true;
        foreach (var payload in line.Payloads)
        {
            if (payload is TextPayload { Text: { } part })
            {
                var skip = first ? Math.Min(keep, part.Length) : 0;
                var ciphered = part[..skip] + CipherText(part[skip..]);
                changed |= ciphered != part;
                result.AddText(ciphered);
            }
            else
            {
                result.Add(payload);
            }

            first = false;
        }

        return changed ? result.Build().Encode() : null;
    }

    /// <summary>Al Bhed, except the placeholders (&lt;t&gt;, &lt;flag&gt;, &lt;se.1&gt;...) the game fills in.</summary>
    private static string CipherText(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            var close = open >= 0 ? text.IndexOf('>', open) : -1;
            if (open < 0 || close < 0)
            {
                sb.Append(Loc.AlBhed(text[i..], keepFormatItems: false));
                break;
            }

            sb.Append(Loc.AlBhed(text[i..open], keepFormatItems: false));
            sb.Append(text, open, close - open + 1);
            i = close + 1;
        }

        return sb.ToString();
    }
}
