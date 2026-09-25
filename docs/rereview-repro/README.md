# Re-review reproducers

The tests behind `docs/RETROSPECTIVE-REREVIEW.md`. Each one asserts the defective behaviour a finding describes, so on the head it was written for (`2746612`, round 85.7) **every test passes**, and a pass means the defect is present. The lines marked `// FINDING` are the ones to invert when a test is turned into a regression test for the fix.

They live here, outside the test projects, so they never build on this branch. To run them against the desk's own branch:

```
git checkout 2746612                      # or a later head
cp docs/rereview-repro/App/*.cs        tests/Patterns.App.Tests/
cp docs/rereview-repro/Core/*.cs       tests/Patterns.Core.Tests/
cp docs/rereview-repro/Rendering/*.cs  tests/Patterns.Rendering.Tests/
dotnet test tests/Patterns.Core.Tests      --filter "FullyQualifiedName~Rereview"
dotnet test tests/Patterns.Rendering.Tests --filter "FullyQualifiedName~Rereview"
dotnet test tests/Patterns.App.Tests       --filter "FullyQualifiedName~Rereview"
```

At `2746612` the result was 19 of 19 (Core), 1 of 1 (Rendering) and 22 of 22 (App), on Linux with the .NET 10.0.112 SDK. A test that fails on a later head means that head behaves otherwise: read its failure before assuming the defect is fixed.

| File | Findings |
|---|---|
| `App/RereviewTakeRepro.cs` | SW-1, SW-2, SW-3, TF-1, TF-2, TF-3, TF-4, TF-6, TF-7 |
| `App/RereviewSecurityRepro.cs` | SEC-2 |
| `App/RereviewEyeRepro.cs` | EY-1, EY-5, EY-6, RT-1 |
| `App/RereviewPersistenceRepro.cs` | PR-2, PB-1, PB-3, PB-4 |
| `Core/RereviewCoreRepro.cs` | EY-1, EY-2, EY-3, WR-2 |
| `Core/RereviewRateRepro.cs` | RT-2, RT-3, TF-8 |
| `Core/RereviewPersistenceCoreRepro.cs` | PB-5, PB-9, PB-10 |
| `Rendering/RereviewPacerRepro.cs` | RT-4 |
