using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The commissioned rig on disk (round 65.9): SAVE KNOWN GOOD writes the rig of the moment as
/// JSON beside the settings; every reading of the facts compares the rig of the day against it,
/// and a comparison whose changes differ from the last one journaled is one journal entry —
/// "the driver changed at 09:12", not a line a second. Nothing here decides anything: the drift
/// is evidence for Super Check, the Machine page, STATE, the bundle and the assistant.
/// </summary>
public sealed class KnownGoodRig
{
    public const string FileName = "patterns.knowngood.json";

    private readonly ShowLog _journal;
    private readonly object _gate = new();
    private string? _lastJournaled;

    public KnownGoodRig(string directory, ShowLog journal)
    {
        Path = System.IO.Path.Combine(directory, FileName);
        _journal = journal;
        Known = Load();
    }

    public string Path { get; }

    /// <summary>The commissioned rig; null until an engineer saves one.</summary>
    public RigSnapshot? Known { get; private set; }

    /// <summary>The last comparison made; null before the first, or when nothing is saved. STATE and the strip read it without probing.</summary>
    public RigDrift? LastDrift { get; private set; }

    /// <summary>One line for a strip or a STATE row: "not saved", or the drift's headline.</summary>
    public string Words => Known is null ? "not saved" : LastDrift?.Headline ?? "saved — not compared yet";

    /// <summary>Saves the rig of the moment as the commissioned one — atomically, the old file kept as .bak.</summary>
    public RigSnapshot Save(RigSnapshot now)
    {
        lock (_gate)
        {
            AtomicFile.WriteAllTextKeepingBackup(Path, now.ToJson());
            Known = now;
            _lastJournaled = null;
            LastDrift = null;
        }
        _journal.Record("rig", "RigKnownGood", "rig", "Saved", $"the rig saved as known good{(now.Note.Length > 0 ? $" — {now.Note}" : "")}: {now.Gpus.Count} GPU, {now.Displays.Count} display{(now.Displays.Count == 1 ? "" : "s")}, {now.Contracts.Count} contract{(now.Contracts.Count == 1 ? "" : "s")}");
        return now;
    }

    /// <summary>Forgets the commissioned rig: the file goes, the comparison stops.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(Path)) File.Delete(Path);
            }
            catch (Exception ex)
            {
                Log.Warn("The known-good file could not be deleted.", ex);
            }
            Known = null;
            _lastJournaled = null;
            LastDrift = null;
        }
    }

    /// <summary>The rig of the day against the commissioned one; null when none was saved. A changed set of changes is journaled once.</summary>
    public RigDrift? Compare(RigSnapshot now)
    {
        RigSnapshot? known;
        lock (_gate)
        {
            known = Known;
        }
        if (known is null) return null;
        var drift = RigDrift.Compare(known, now);
        var key = string.Join("|", drift.Changes);
        lock (_gate)
        {
            LastDrift = drift;
            if (key == _lastJournaled) return drift;
            _lastJournaled = key;
        }
        if (drift.Same) _journal.Record("rig", "RigDrift", "rig", "Same", drift.Headline);
        else _journal.Record("rig", "RigDrift", "rig", "Changed", $"{drift.Headline}: {string.Join("; ", drift.Changes)}");
        return drift;
    }

    private RigSnapshot? Load()
    {
        try
        {
            return File.Exists(Path) ? RigSnapshot.FromJson(File.ReadAllText(Path)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("The known-good file could not be read.", ex);
            return null;
        }
    }
}
