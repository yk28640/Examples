using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ServiceV14
{
    public sealed class OperationLogEntry
    {
        public DateTime? TimeCreated { get; set; }
        public string User { get; set; }
        public string Computer { get; set; }
        public string EventContent { get; set; }
        public long EventId { get; set; }
        public string Level { get; set; }
        public string Provider { get; set; }
        public string RecordId { get; set; }
    }

    public sealed class EventLogService
    {
        public const int DefaultEntries = 100;
        public const int MaximumEntries = 5000;

        public IReadOnlyList<OperationLogEntry> ReadOperationLogs(int maximumEntries = DefaultEntries,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (maximumEntries < 1 || maximumEntries > MaximumEntries)
                throw new ArgumentOutOfRangeException(nameof(maximumEntries));

            cancellationToken.ThrowIfCancellationRequested();
            var query = new EventLogQuery("SiemensAG-Automation-TIAPortal/Operational", PathType.LogName)
            {
                ReverseDirection = true
            };
            var entries = new List<OperationLogEntry>();
            using (var reader = new EventLogReader(query))
            {
                while (entries.Count < maximumEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var record = reader.ReadEvent())
                    {
                        if (record == null) break;
                        entries.Add(CreateEntry(record));
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }

        private static OperationLogEntry CreateEntry(EventRecord record)
        {
            var xml = TryGetXml(record);
            return new OperationLogEntry
            {
                TimeCreated = record.TimeCreated,
                User = GetUserName(record, xml),
                Computer = record.MachineName ?? "",
                EventContent = TryGetDescription(record) ?? GetEventData(xml) ?? xml ?? "",
                EventId = record.Id,
                Level = record.LevelDisplayName ?? "",
                Provider = record.ProviderName ?? "",
                RecordId = record.RecordId?.ToString() ?? ""
            };
        }

        private static string GetUserName(EventRecord record, string xml)
        {
            var sid = record.UserId?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                try
                {
                    return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
                }
                catch (IdentityNotMappedException)
                {
                    return sid;
                }
            }
            if (string.IsNullOrWhiteSpace(xml)) return "";
            try
            {
                var document = XDocument.Parse(xml);
                return document.Descendants().Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "UserID")?.Value
                    ?? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "UserID")?.Value
                    ?? "";
            }
            catch (XmlException)
            {
                return "";
            }
        }

        private static string TryGetDescription(EventRecord record)
        {
            try { return record.FormatDescription(); }
            catch (EventLogException) { return null; }
        }

        private static string TryGetXml(EventRecord record)
        {
            try { return record.ToXml(); }
            catch (EventLogException) { return null; }
        }

        private static string GetEventData(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var values = XDocument.Parse(xml).Descendants()
                    .Where(element => element.Name.LocalName == "Data" || element.Name.LocalName == "Message")
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
    }
}
