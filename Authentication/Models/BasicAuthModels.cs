// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>User name and password for HTTP Basic authentication.</summary>
    /// <remarks>Immutable, and checked once, when created.</remarks>
    public sealed class BasicAuthCredentials
    {
        /// <summary>The user name.</summary>
        public string Login { get; }

        /// <summary>The password.</summary>
        public string Password { get; }

        /// <summary>Creates a credential pair.</summary>
        /// <param name="login">The user name. It may not contain <c>:</c>.</param>
        /// <param name="password">The password. A <c>:</c> is fine: the server splits at the first one.</param>
        /// <exception cref="ArgumentException">
        /// Either is empty, or the user name contains <c>:</c>, which RFC 7617 forbids - the server
        /// would read everything after it as the password.
        /// </exception>
        public BasicAuthCredentials(string login, string password)
        {
            if (string.IsNullOrEmpty(login))
                throw new ArgumentException("The login must not be empty", nameof(login));

            if (password is null)
                throw new ArgumentNullException(nameof(password));

            if (login.IndexOf(':') != -1)
                throw new ArgumentException("Symbol ':' is not allowed in Basic auth", nameof(login));

            Login = login;
            Password = password;
        }
    }
}
