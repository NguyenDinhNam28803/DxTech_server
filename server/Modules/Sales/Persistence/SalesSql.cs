using server.Infrastructure.Persistence.Conversions;
using server.Modules.Sales.Domain;

namespace server.Modules.Sales.Persistence;

/// <summary>Biểu thức SQL dùng chung cho CHECK của module Sales.</summary>
internal static class SalesSql
{
    public static string StatusCheck(string column) => SnakeCaseEnumConverter<OrderStatus>.CheckSql(column);

    public static string Status(OrderStatus status) => $"'{SnakeCaseEnumConverter<OrderStatus>.ToProvider(status)}'";
}
