using System.Xml.Linq;

namespace CloudInlet.Core.Notifications;

public sealed record NotificationButton(string Text, NotificationCommand Command);
public sealed record NotificationNotice(string Group, string Tag, string Title, string Message,
    NotificationCommand Command, IReadOnlyList<NotificationButton> Buttons);
public sealed record NotificationBatch(IReadOnlyList<NotificationNotice> Notices, IReadOnlyList<string> ClearGroups);

public static class NotificationContent
{
    public static string CreateXml(NotificationNotice notice, bool sound)
    {
        if (notice.Buttons.Count > 2) throw new ArgumentException("Use at most two focused notification actions.");
        var toast = new XElement("toast", new XAttribute("launch", NotificationCommandCodec.Encode(notice.Command)),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", notice.Title), new XElement("text", notice.Message))));
        if (notice.Buttons.Count > 0)
            toast.Add(new XElement("actions", notice.Buttons.Select(button => new XElement("action",
                new XAttribute("content", button.Text), new XAttribute("arguments", NotificationCommandCodec.Encode(button.Command)),
                new XAttribute("activationType", "foreground")))));
        if (!sound) toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }
}
