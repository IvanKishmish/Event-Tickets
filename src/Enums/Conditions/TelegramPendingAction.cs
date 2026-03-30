namespace EventTickets.Enums.Conditions;

public enum TelegramPendingAction
{
    AwaitingOrderId,
    AwaitingBuyQuantity,
    AwaitingBuyEmail,
    AwaitingHistoryEmail,
    AwaitingNewEventJson
}