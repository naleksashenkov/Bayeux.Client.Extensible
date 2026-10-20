# Installing

Which build your application gets, what .NET Framework needs, and how to use the library in a
host that does not resolve a library's dependencies for you.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

## With NuGet

```bash
dotnet add package Bayeux.Client.Extensible --prerelease
```

NuGet picks the build for your framework and brings its dependencies:

| Your application | Build | Dependencies it brings |
|---|---|---|
| .NET 10 | `net10.0` | `Microsoft.Extensions.Logging.Abstractions` |
| .NET Core 2.0 – .NET 9 | `netstandard2.0` | the above, `System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces` |
| .NET Framework 4.7.2+ | `net472` | the same |
| .NET Framework 4.6.1 – 4.7.1 | `netstandard2.0` | the same |

Nothing else is needed. On .NET 5+ a newer framework copy of `System.Text.Json` serves the library
as is.

## On .NET Framework: binding redirects

.NET Framework loads a strong-named assembly only at the exact version a library was compiled
against, unless a binding redirect says otherwise. This library is compiled against
`System.Text.Json` **8.0.0.0**, while the package depends on **8.0.5** — the patched version — so
an application gets 8.0.5 and needs a redirect to it:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Text.Json" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-8.0.0.5" newVersion="8.0.0.5" />
</dependentAssembly>
```

**You do not write it yourself.** The build generates it into `app.config` or `web.config`, as it
does for every package with such dependencies. One case needs a setting: an older ASP.NET project
(a non-SDK `.csproj` with a `web.config`) that uses `PackageReference`. Turn the generation on:

```xml
<PropertyGroup>
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
```

Without it, Visual Studio warns about the conflict and offers to add the redirects.

**Why 8.0.0.0?** A host may ship its own `System.Text.Json` 8.0.0.0 with no redirect. Such a host
refuses a library compiled against any later 8.0.x, even when the code is the same. Compiling
against the lowest 8.0 lets such hosts load the library, and lets everyone else run it on the
patched version.

## In a host that does not resolve dependencies

Some hosts take a DLL rather than a NuGet package. Examples:
- plugin hosts;
- scripting engines;
- platforms that compile your code against their own assemblies.

Such a host neither copies the library's dependencies nor adds references to them. Two things are
then up to you.

### 1. The DLLs to deploy

Take the library from the package: open the `.nupkg` as a zip.
- `lib/net472/Bayeux.Client.Extensible.dll` for a .NET Framework host;
- `lib/netstandard2.0/` for a .NET Core or .NET 5+ host.

**A host that ships these assemblies itself serves the library with its own copies.** This holds
when each one is at the version below or later and, on .NET Framework, has a redirect to it.
Deploy only those it lacks — never replace an assembly the host ships.

| Assembly | Minimum version (`net472`) |
|---|---|
| `System.Text.Json` | 8.0.0.0 |
| `Microsoft.Bcl.AsyncInterfaces` | 8.0.0.0 |
| `Microsoft.Extensions.Logging.Abstractions` | 2.1.1.0 |
| `System.Text.Encodings.Web` | 8.0.0.0 |
| `System.Memory` | 4.0.1.2 |
| `System.Buffers` | 4.0.3.0 |
| `System.Numerics.Vectors` | 4.1.4.0 |
| `System.Runtime.CompilerServices.Unsafe` | 6.0.0.0 |
| `System.Threading.Tasks.Extensions` | 4.2.0.1 |

To see what a .NET Framework host ships, read the versions from its `bin` folder in PowerShell:

```powershell
Get-ChildItem <host>\bin -Filter *.dll |
    ForEach-Object { "{0}  {1}" -f $_.Name, [Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version }
```

### 2. The references to compile against

Code that uses the client needs references to the assemblies its public API exposes:

| Reference | Why |
|---|---|
| `System.Net.Http` | `HttpClient`, passed to the constructor. On .NET Framework it is not referenced by default |
| `System.Text.Json` | `JsonSerializerOptions` in the options, `JsonElement` in events |
| `Microsoft.Bcl.AsyncInterfaces` | `IAsyncDisposable`, which the client implements (.NET Framework, netstandard2.0) |
| `Microsoft.Extensions.Logging.Abstractions` | the optional `ILogger<BayeuxClient>` in the constructor |

Without them the compiler stops with CS0012 ("must add a reference to assembly …"). In an MSBuild
project, point the references at the host's own copies:

```xml
<ItemGroup>
  <Reference Include="System.Net.Http" />
  <Reference Include="System.Text.Json">
    <HintPath>$(HostBinPath)/System.Text.Json.dll</HintPath>
    <SpecificVersion>False</SpecificVersion>
    <Private>False</Private>
  </Reference>
  <Reference Include="Microsoft.Bcl.AsyncInterfaces">
    <HintPath>$(HostBinPath)/Microsoft.Bcl.AsyncInterfaces.dll</HintPath>
    <SpecificVersion>False</SpecificVersion>
    <Private>False</Private>
  </Reference>
  <Reference Include="Microsoft.Extensions.Logging.Abstractions">
    <HintPath>$(HostBinPath)/Microsoft.Extensions.Logging.Abstractions.dll</HintPath>
    <SpecificVersion>False</SpecificVersion>
    <Private>False</Private>
  </Reference>
</ItemGroup>
```

`$(HostBinPath)` stands for wherever the host keeps its assemblies. `Private` is `False` so that
the build copies nothing: at runtime the host's copies are the ones that load.

### Example: Creatio 8.3 (.NET Framework)

Checked against Creatio 8.3.0, .NET Framework edition. Creatio ships every dependency above at the
minimum version or later, with the redirects in place. So:

- **Package type:** an assembly package.
- **What to add:** `Bayeux.Client.Extensible.dll`, as an external assembly of the package. It
  lands in `Assemblies/Bayeux.Client.Extensible/`, and the package `.csproj` picks it up. Add no
  other DLL.
- **References:** in the package `.csproj`, under `<ItemGroup Label="3rd Party References">`, add
  the four references above with `$(CoreLibPath)` in place of `$(HostBinPath)`.
- **`Web.config`:** no change.

On the request thread of a Creatio service, do not block on the client's tasks directly. Run them
off the ASP.NET context — `Task.Run(() => …).GetAwaiter().GetResult()` — or make the service
asynchronous.
