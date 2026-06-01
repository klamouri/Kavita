
# Scripts

This directory contains useful scripts used for development or debugging user issues.

| Script | Purpose |
|---|---|
| `file_layout_dump.py` | Dump a JSON map of a local library's file structure (for debugging scanner issues). |
| `generate_readinglists.py` | Populate a local Kavita instance with randomized test reading lists. |
| `generate-test-library.py` | Render the synthetic strict-parser catalog into real `.cbz` files with covers and fake pages, making it easy to build a reproducible UI test dataset for screenshots or bug reports. Invoked by the `GenerateTestLibrary` MSBuild target in `Kavita.Services.Tests.csproj`. See `Kavita.Services.Tests/Test Data/StrictParser/README.md`. |
