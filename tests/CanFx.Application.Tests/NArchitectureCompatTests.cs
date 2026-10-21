using CanFx.Application.Exceptions;
using CanFx.Application.Responses;
using CanFx.Domain.Exceptions;
using CanFx.Domain.Results;
using CanFx.Persistence.Paging;

namespace CanFx.Application.Tests;

public class NArchitectureCompatTests
{
    [Fact]
    public void OrThrow_returns_value_on_success()
    {
        Result<int> result = 42;
        Assert.Equal(42, result.OrThrow());
    }

    [Fact]
    public void OrThrow_maps_error_types_to_exceptions()
    {
        Assert.IsType<NotFoundException>(Throw(Error.NotFound("x.not_found", "yok")));
        Assert.IsType<ConflictException>(Throw(Error.Conflict("x.taken", "var")));
        Assert.Equal("x.rule", Assert.IsType<BusinessException>(Throw(Error.Failure("x.rule", "kural"))).Code);

        Result<int> invalid = new[] { Error.Validation("v", "boş olamaz", "Name"), Error.Validation("v", "çok uzun", "Name") };
        var validation = Assert.Throws<ValidationException>(() => invalid.OrThrow());
        Assert.Equal(new[] { "boş olamaz", "çok uzun" }, validation.Errors["Name"]);

        static Exception Throw(Error error)
        {
            Result<int> result = error;
            return Assert.ThrowsAny<Exception>(() => result.OrThrow());
        }
    }

    [Fact]
    public void ToGetListResponse_copies_paging()
    {
        IPaginate<int> page = new Paginate<int>([1, 2, 3], index: 1, size: 3, count: 10);

        GetListResponse<string> response = page.ToGetListResponse(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(new[] { "1", "2", "3" }, response.Items);
        Assert.Equal(1, response.Index);
        Assert.Equal(3, response.Size);
        Assert.Equal(10, response.Count);
        Assert.Equal(4, response.Pages);
        Assert.True(response.HasPrevious);
        Assert.True(response.HasNext);
    }
}
