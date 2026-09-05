using Patterns.Core.Services;

namespace Patterns.Core.LowerThirds;

/// <summary>What an import made of a people sheet: the entries, and the notes an operator should read.</summary>
public sealed class LowerThirdLibraryImport
{
    public List<LowerThirdEntry> Entries { get; } = new();

    /// <summary>"Row 4: no name — skipped."</summary>
    public List<string> Notes { get; } = new();

    public int Rows { get; set; }

    public string Summary
    {
        get
        {
            var people = $"{Entries.Count} {(Entries.Count == 1 ? "entry" : "entries")} from {Rows} row{(Rows == 1 ? "" : "s")}";
            return Notes.Count == 0 ? people : $"{people} — {Notes.Count} note{(Notes.Count == 1 ? "" : "s")}";
        }
    }
}

/// <summary>
/// The lower-thirds library as a spreadsheet, both ways: a speaker list (a CSV or the first
/// sheet of an .xlsx) becomes entries, the library goes out as the same columns, and a template
/// shows the columns with a few rows to copy. Pure — the caller decides where the entries go.
/// </summary>
public static class LowerThirdLibrary
{
    public static readonly string[] Headers = { "Name", "Role", "Company", "Photo", "Note" };

    private static readonly string[] NameHeaders = { "Name", "Full name", "Person", "Speaker", "Presenter", "Guest", "Who", "Headline" };
    private static readonly string[] FirstNameHeaders = { "First name", "First", "Given name", "Forename" };
    private static readonly string[] LastNameHeaders = { "Last name", "Last", "Surname", "Family name" };
    private static readonly string[] RoleHeaders = { "Role", "Title", "Job title", "Job", "Position", "Function", "Subtitle", "Line 2", "Second line" };
    private static readonly string[] CompanyHeaders = { "Company", "Organisation", "Organization", "Org", "Affiliation", "Employer", "From" };
    private static readonly string[] PhotoHeaders = { "Photo", "Headshot", "Picture", "Image", "Portrait", "File", "Photo file" };
    private static readonly string[] NoteHeaders = { "Note", "Notes", "Comment", "Comments", "Remarks", "Pronunciation" };

    /// <summary>
    /// Every row with a name becomes an entry (a First name and a Last name column are joined
    /// when there is no Name column); a row without one is noted and skipped. Nothing is
    /// resolved — a photo path is kept as written.
    /// </summary>
    public static LowerThirdLibraryImport Import(TableData table)
    {
        var result = new LowerThirdLibraryImport();
        if (table.Headers.Count == 0)
        {
            result.Notes.Add("The file has no header row — the first row must name the columns (download the template).");
            return result;
        }
        var hasName = table.Column(NameHeaders) >= 0;
        var hasSplitName = table.Column(FirstNameHeaders) >= 0 || table.Column(LastNameHeaders) >= 0;
        if (!hasName && !hasSplitName)
        {
            result.Notes.Add("No Name column was found — the columns are read by their header names (download the template).");
            return result;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var rowNo = r + 2; // the header is row 1 in the sheet
            result.Rows++;
            var name = hasName ? table.Get(r, NameHeaders) : "";
            if (name.Length == 0 && hasSplitName)
            {
                name = string.Join(' ', new[] { table.Get(r, FirstNameHeaders), table.Get(r, LastNameHeaders) }.Where(s => s.Length > 0));
            }
            if (name.Length == 0)
            {
                result.Notes.Add($"Row {rowNo}: no name — skipped.");
                continue;
            }
            result.Entries.Add(new LowerThirdEntry
            {
                Name = name,
                Role = table.Get(r, RoleHeaders),
                Company = table.Get(r, CompanyHeaders),
                Photo = table.Get(r, PhotoHeaders),
                Note = table.Get(r, NoteHeaders),
            });
        }
        return result;
    }

    /// <summary>
    /// Imported entries into a library: an entry whose name is already there updates that one
    /// (its id, and every cue that names it, stay; a field the list leaves empty keeps what it
    /// had), a new name is added at the end.
    /// </summary>
    public static (int Added, int Updated) Merge(ICollection<LowerThirdEntry> into, IEnumerable<LowerThirdEntry> entries)
    {
        int added = 0, updated = 0;
        foreach (var e in entries)
        {
            var existing = into.FirstOrDefault(x => string.Equals(x.Name.Trim(), e.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                into.Add(e);
                added++;
                continue;
            }
            existing.Name = e.Name;
            if (e.Role.Length > 0) existing.Role = e.Role;
            if (e.Company.Length > 0) existing.Company = e.Company;
            if (e.Photo.Length > 0) existing.Photo = e.Photo;
            if (e.Note.Length > 0) existing.Note = e.Note;
            updated++;
        }
        return (added, updated);
    }

    /// <summary>The columns and a few rows that show what the IMPORT reads.</summary>
    public static string Template()
    {
        var rows = new List<IEnumerable<string>>
        {
            Headers,
            new[] { "Jane Doe", "Chief Executive", "Acme Ltd", @"C:\show\headshots\jane.jpg", "Opens the day — pronounced DOH" },
            new[] { "Sam Patel", "Head of Product", "", "", "No company: the brand kit's shows; no photo: the design's picture stays" },
            new[] { "Doors open 19:00", "Tonight", "", "", "A line of information works the same way" },
        };
        return CsvTable.Write(rows);
    }

    /// <summary>The library as the same columns, ready for Excel, a printout, or a round trip.</summary>
    public static string Export(IEnumerable<LowerThirdEntry> entries)
    {
        var rows = new List<IEnumerable<string>> { Headers };
        foreach (var e in entries)
        {
            rows.Add(new[] { e.Name, e.Role, e.Company, e.Photo, e.Note });
        }
        return CsvTable.Write(rows);
    }
}
