using Backend.Controllers;
using Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Backend.Tests;

public class AlertsControllerTests
{
    [Fact]
    public async Task GetAlerts_PaginatesWithSkip_AndReportsFilteredTotal()
    {
        await using var db = Db();
        db.AppAlerts.AddRange(Enumerable.Range(0, 45).Select(i => Alert(i)));
        await db.SaveChangesAsync();
        var controller = new AlertsController(db);

        var page1 = Payload((await controller.GetAlerts("default", take: 30)).Result);
        Assert.Equal(45, page1.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(30, page1.RootElement.GetProperty("items").GetArrayLength());

        var page2 = Payload((await controller.GetAlerts("default", take: 30, skip: 30)).Result);
        Assert.Equal(45, page2.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(15, page2.RootElement.GetProperty("items").GetArrayLength());

        var page1Ids = page1.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
        var page2Ids = page2.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    [Fact]
    public async Task GetAlerts_SkipIsClampedAtZero_AndSkipBeyondTotalReturnsEmptyPage()
    {
        await using var db = Db();
        db.AppAlerts.AddRange(Enumerable.Range(0, 5).Select(i => Alert(i)));
        await db.SaveChangesAsync();
        var controller = new AlertsController(db);

        var negative = Payload((await controller.GetAlerts("default", take: 30, skip: -10)).Result);
        Assert.Equal(5, negative.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(5, negative.RootElement.GetProperty("items").GetArrayLength());

        var beyond = Payload((await controller.GetAlerts("default", take: 30, skip: 99)).Result);
        Assert.Equal(5, beyond.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetAlerts_IncludeArchived_ChangesItemsAndTotal()
    {
        await using var db = Db();
        db.AppAlerts.AddRange(Enumerable.Range(0, 4).Select(i => Alert(i)));
        var archived = Alert(99);
        archived.ArchivedAtUtc = DateTime.UtcNow;
        db.AppAlerts.Add(archived);
        await db.SaveChangesAsync();
        var controller = new AlertsController(db);

        var activeOnly = Payload((await controller.GetAlerts("default", take: 30)).Result);
        Assert.Equal(4, activeOnly.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(4, activeOnly.RootElement.GetProperty("items").GetArrayLength());

        var withArchived = Payload((await controller.GetAlerts("default", take: 30, includeArchived: true)).Result);
        Assert.Equal(5, withArchived.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(5, withArchived.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetAlerts_TotalRespectsUserAndUnreadFilters()
    {
        await using var db = Db();
        db.AppAlerts.AddRange(Enumerable.Range(0, 6).Select(i => Alert(i)));
        var other = Alert(100);
        other.UserId = "other";
        var read = Alert(101);
        read.IsRead = true;
        db.AppAlerts.AddRange(other, read);
        await db.SaveChangesAsync();
        var controller = new AlertsController(db);

        var unread = Payload((await controller.GetAlerts("default", take: 2, unreadOnly: true)).Result);
        Assert.Equal(6, unread.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, unread.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(6, unread.RootElement.GetProperty("unreadCount").GetInt32());
    }

    private static JsonDocument Payload(IActionResult? result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static AppAlert Alert(int seconds) => new()
    {
        Id = Guid.NewGuid(), UserId = "default", Type = "sequence_rule", Title = "t",
        Message = "m", PriceSnapshot = 10, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-seconds)
    };

    private static AppDbContext Db()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
