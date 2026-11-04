using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Mail
{
    /// <summary>Sends one plain-text message. Swapped for a recorder by the test harness.</summary>
    internal interface IMailSender
    {
        Task SendAsync(EmailSettings settings, string to, string subject, string body, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Outgoing mail through <see cref="SmtpClient"/>, which ships with .NET.
    ///
    /// <para><b>Why not MailKit.</b> Bonfire is one DLL in one zip, and the release checks
    /// that zip byte for byte. MailKit would add MimeKit and BouncyCastle beside it, three
    /// more assemblies for Jellyfin's plugin loader to resolve against whatever else is
    /// installed. SmtpClient covers what home servers use: STARTTLS on 587, or a plain relay
    /// on the local network.</para>
    ///
    /// <para><b>What it can't do: port 465.</b> That port speaks TLS from the first byte,
    /// and SmtpClient only knows how to upgrade a plain connection, so it sits waiting for a
    /// greeting that never comes. The settings route refuses 465 with STARTTLS rather than
    /// letting a test hang for its whole timeout. Every common provider also offers 587.</para>
    /// </summary>
    internal sealed class SmtpMailSender : IMailSender
    {
        /// <summary>Long enough for a slow provider, short enough that a test returns.</summary>
        internal const int TimeoutMs = 20000;

        public async Task SendAsync(EmailSettings settings, string to, string subject, string body, CancellationToken cancellationToken)
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress.Trim(),
                    string.IsNullOrWhiteSpace(settings.FromName) ? "Bonfire" : settings.FromName.Trim()),
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
                BodyEncoding = System.Text.Encoding.UTF8,
                SubjectEncoding = System.Text.Encoding.UTF8,
            };
            message.To.Add(new MailAddress(to));

            using var client = new SmtpClient(settings.Host.Trim(), settings.Port)
            {
                EnableSsl = EmailSecurity.Normalize(settings.Security) == EmailSecurity.StartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = TimeoutMs,
                UseDefaultCredentials = false,
            };
            if (!string.IsNullOrEmpty(settings.Username))
            {
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeoutMs);
            await client.SendMailAsync(message, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Where Bonfire's mail goes out.</summary>
    internal static class Mailer
    {
        /// <summary>The real sender. Replaced by reflection in tests/cs/email.</summary>
        internal static IMailSender Sender = new SmtpMailSender();

        /// <summary>
        /// Sends with the settings given, or throws. The message a person sees comes from
        /// <see cref="Describe"/>, never the exception text, which can carry the server's
        /// whole reply.
        /// </summary>
        internal static Task SendAsync(EmailSettings settings, string to, string subject, string body, CancellationToken cancellationToken = default)
            => Sender.SendAsync(settings, to, subject, body, cancellationToken);

        /// <summary>A failure in words an administrator can act on.</summary>
        internal static string Describe(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;

            return ex switch
            {
                OperationCanceledException => "The mail server didn't answer in time. Check the server and port.",
                SmtpFailedRecipientException => "The mail server refused that address.",
                SmtpException s when s.StatusCode == SmtpStatusCode.MustIssueStartTlsFirst
                    => "The mail server wants an encrypted connection. Choose STARTTLS.",
                SmtpException s when (int)s.StatusCode == 535 || (s.Message ?? string.Empty).Contains("authentic", StringComparison.OrdinalIgnoreCase)
                    => "The mail server didn't accept the username or password. Some providers need an app password.",
                SmtpException when inner is System.Net.Sockets.SocketException
                    => "Couldn't reach the mail server. Check the server and port.",
                SmtpException when inner is System.Security.Authentication.AuthenticationException
                    => "The secure connection failed. Check the port: 587 for STARTTLS.",
                FormatException => "That address isn't valid.",
                _ => "The mail server refused the message."
            };
        }

        /// <summary>A real-looking address, short enough to store. Not a full RFC check: the
        /// confirmation code is what proves an address works.</summary>
        internal static bool IsValidAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            address = address.Trim();
            if (address.Length > 254 || address.IndexOfAny(new[] { '\r', '\n', ',', ';', ' ' }) >= 0) return false;
            try
            {
                var parsed = new MailAddress(address);
                return string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase)
                    && parsed.Host.Contains('.', StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>"l•••@gmail.com": enough to recognise, not enough to harvest.</summary>
        internal static string Mask(string address)
        {
            var at = address.IndexOf('@');
            if (at <= 0) return "•••";
            return address.Substring(0, 1) + "•••" + address.Substring(at);
        }
    }
}
