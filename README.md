# BoringSsl.Net.CurlImpersonate Package Workspace

This folder is the standalone package workspace for `BoringSsl.Net.CurlImpersonate`.

## Layout

- `src/BoringSsl.Net.CurlImpersonate`: package project
- `tests/BoringSsl.Net.CurlImpersonate.UnitTests`: unit tests
- `tests/BoringSsl.Net.CurlImpersonate.IntegrationTests`: integration tests
- `BoringSsl.Net.CurlImpersonate.Package.slnx`: package-local solution

## Build

```bash
dotnet restore BoringSsl.Net.CurlImpersonate.Package.slnx
dotnet build BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
dotnet test BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
dotnet pack src/BoringSsl.Net.CurlImpersonate/BoringSsl.Net.CurlImpersonate.csproj -c Release -o artifacts/nuget
```
