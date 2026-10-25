namespace Pcl.Core.Ports.Operations;
public sealed record JournalEntry(string OperationId, long Sequence, string Level, string Code, bool Truncated = false);
public interface IOperationJournal
{
    void Write(JournalEntry entry);
    IReadOnlyList<JournalEntry> Read(string operationId);
}
