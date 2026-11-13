using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.UI;

/// <summary>
/// Easter egg: with the plugin in Al Bhed, the messages you write show up in Al Bhed in your own chat window. Display
/// only: what the other players receive is unchanged, nothing else is sent to the server.
/// </summary>
public sealed class AlBhedChat : IDisposable
{
    private readonly IChatGui chat;

    public AlBhedChat(IChatGui chat)
    {
        this.chat = chat;
        chat.ChatMessage += OnChatMessage;
    }

    /// <summary>Your messages already go out in Al Bhed (see <see cref="AlBhedSender"/>): not ciphered again here.</summary>
    public bool SentInAlBhed { get; set; }

    public void Dispose() => chat.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (SentInAlBhed || Loc.Code != "al" || !IsPlayerChat(message.LogKind) || !WrittenByYou(message))
            return;

        message.Message = Cipher(message.Message);
    }

    /// <summary>
    /// Your own message: marked as coming from you, or with your name as sender (the mark is not set in every channel).
    /// Outgoing tells are always yours.
    /// </summary>
    private static bool WrittenByYou(IHandleableChatMessage message)
    {
        if (message.LogKind == XivChatType.TellOutgoing || message.SourceKind == XivChatRelationKind.LocalPlayer)
            return true;

        var name = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        return !string.IsNullOrEmpty(name) && message.Sender.TextValue.Contains(name, StringComparison.Ordinal);
    }

    /// <summary>The channels players write in (not system messages, battle log or emotes).</summary>
    private static bool IsPlayerChat(XivChatType type) => type is
        XivChatType.Say or XivChatType.Shout or XivChatType.Yell or XivChatType.TellOutgoing or XivChatType.Party
        or XivChatType.CrossParty or XivChatType.Alliance or XivChatType.FreeCompany or XivChatType.NoviceNetwork
        or XivChatType.PvPTeam or XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4
        or XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8 or XivChatType.CrossLinkShell1
        or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3 or XivChatType.CrossLinkShell4
        or XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6 or XivChatType.CrossLinkShell7
        or XivChatType.CrossLinkShell8;

    /// <summary>The message with its plain text in Al Bhed; item links, auto-translate phrases and icons are kept.</summary>
    private static SeString Cipher(SeString message)
    {
        var result = new SeStringBuilder();
        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload { Text: { } text })
                result.AddText(Loc.AlBhed(text, keepFormatItems: false));
            else
                result.Add(payload);
        }

        return result.Build();
    }
}
