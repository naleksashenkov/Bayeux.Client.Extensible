// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// What the library asks of its host. A .NET Framework host that brings its own copies of the
/// dependencies - Creatio is one - loads the library only if it asks for no more than the host has.
/// </summary>
/// <remarks>Runs on net472 only: on .NET 10 System.Text.Json is part of the framework.</remarks>
public class DependencyTests
{
#if NETFRAMEWORK
    [Fact]
    public void The_library_is_compiled_against_System_Text_Json_8_0_0()
    {
        // Creatio 8.3 ships System.Text.Json 8.0.0.0 without a redirect, and refuses a library
        // that references any later 8.0.x (CS1705). The project file compiles against 8.0.0
        // while the package depends on 8.0.5; an upgrade that drops that would pass every other
        // test and break such hosts silently.
        var reference = typeof(BayeuxClient).Assembly.GetReferencedAssemblies()
            .Single(assembly => assembly.Name == "System.Text.Json");

        Assert.Equal(new Version(8, 0, 0, 0), reference.Version);
    }

    [Fact]
    public void An_application_runs_it_on_the_patched_System_Text_Json()
    {
        // The redirect generated for the application lifts the 8.0.0.0 reference to the version
        // the package depends on.
        var loaded = typeof(System.Text.Json.JsonSerializer).Assembly.GetName().Version;

        Assert.NotNull(loaded);
        Assert.True(loaded >= new Version(8, 0, 0, 5), $"Loaded System.Text.Json {loaded}.");
    }
#endif
}
