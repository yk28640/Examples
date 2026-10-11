using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace AutomationPlatform;

public sealed record OperationLogEntry(
    DateTime? TimeCreated,
    string User,
    string Computer,
    string EventContent,
    long EventId,
    string Level,
    string Provider,
    string RecordId);

public sealed class EventLogService
{

    private const int MaximumEntries = 5000;

    public IReadOnlyList<OperationLogEntry> ReadOperationLogs() => ReadOperationLogs(MaximumEntries);

    public IReadOnlyList<OperationLogEntry> ReadOperationLogs(int maximumEntries, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);

        var query = new EventLogQuery("SiemensAG-Automation-TIAPortal/Operational",
            PathType.LogName)
        {
            ReverseDirection = true
        };

        var entries = new List<OperationLogEntry>();
        using var reader = new EventLogReader(query);
        while (entries.Count < maximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var eventRecord = reader.ReadEvent();
            if (eventRecord is null)
                break;

            entries.Add(CreateEntry(eventRecord));
        }

        return entries;
    }

    private static OperationLogEntry CreateEntry(EventRecord eventRecord)
    {
        var eventXml = TryGetXml(eventRecord);
        var user = GetUserName(eventRecord, eventXml);
        var content = TryGetDescription(eventRecord) ?? GetEventData(eventXml) ?? eventXml ?? "";

        return new OperationLogEntry(
            eventRecord.TimeCreated,
            user,
            eventRecord.MachineName ?? "",
            content,
            eventRecord.Id,
            eventRecord.LevelDisplayName ?? "",
            eventRecord.ProviderName ?? "",
            eventRecord.RecordId?.ToString() ?? "");
    }

    private static string GetUserName(EventRecord eventRecord, string? eventXml)
    {
        var securityIdentifier = eventRecord.UserId?.Value;
        if (!string.IsNullOrWhiteSpace(securityIdentifier))
        {
            try
            {
                return new SecurityIdentifier(securityIdentifier)
                    .Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                return securityIdentifier;
            }
        }

        var userId = GetXmlValue(eventXml, "UserID");
        return userId ?? "";
    }

    private static string? TryGetDescription(EventRecord eventRecord)
    {
        try
        {
            return eventRecord.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static string? TryGetXml(EventRecord eventRecord)
    {
        try
        {
            return eventRecord.ToXml();
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static string? GetEventData(string? eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(eventXml);
            var values = document.Descendants()
                .Where(element => element.Name.LocalName is "Data" or "Message")
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0);

            var result = string.Join(Environment.NewLine, values);
            return result.Length > 0 ? result : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? GetXmlValue(string? eventXml, string localName)
    {
        if (string.IsNullOrWhiteSpace(eventXml))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(eventXml)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == localName)
                ?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
