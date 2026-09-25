// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication.Models
{
    /// <summary>User name and password for HTTP Basic authentication.</summary>
    public class BasicAuthCredentials
    {
        /// <summary>The user name.</summary>
        public string Login { get; set; }

        /// <summary>The password.</summary>
        public string Password { get; set; }

        /// <summary>Creates a credential pair.</summary>
        /// <param name="login">The user name.</param>
        /// <param name="password">The password.</param>
        public BasicAuthCredentials(string login, string password)
        {
            Login = login;
            Password = password;
        }
    }
}
