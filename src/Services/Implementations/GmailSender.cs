using System.Net;
using System.Net.Mail;
using EventTickets.Logs;
using EventTickets.Services.Abstractions;

namespace EventTickets.Services.Implementations;

public class GmailSender(string userEmail, string appCode) : IMailSender
{
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;
    private const bool SmtpIsSsl = true;
    
    public async Task<bool> SendMailAsync(string subject, string body, bool isHtml, IEnumerable<string> recipients)
    {
        try
        {
            using MailMessage mailMessage = new MailMessage();
            mailMessage.From = new MailAddress(userEmail);
            mailMessage.Subject = subject;
            mailMessage.Body = body;
            mailMessage.IsBodyHtml = isHtml;

            foreach (string r in recipients)
                mailMessage.To.Add(r);

            using SmtpClient smtpClient = new SmtpClient(SmtpHost, SmtpPort);
            smtpClient.Credentials = new NetworkCredential(userEmail, appCode);
            smtpClient.EnableSsl = SmtpIsSsl;

            await smtpClient.SendMailAsync(mailMessage);

            return true;
        }
        catch(Exception ex)
        {
            // Console.ForegroundColor = ConsoleColor.Red;
            // Console.WriteLine($"[Email Error]: {ex.Message}");
            // Console.ResetColor();
            ConcurrentLogger.Log($"[Email Error]: {ex.Message}", ConsoleColor.Red);
            return false;
        }
    }

}