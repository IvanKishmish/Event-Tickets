namespace EventTickets.Services.Abstractions;

public interface IMailSender
{
    Task<bool> SendMailAsync(string subject, string body, bool isHtml, IEnumerable<string> recipients);
}