using Can.Core.Domain.Results;

namespace Can.Core.Domain.Tests;

public class ResultTests
{
    private static readonly Error NotFound = Error.NotFound("item.not_found", "Bulunamadı.");
    private static readonly Error TooCheap = Error.Failure("item.too_cheap", "Fiyat çok düşük.");

    private static Result<int> Parse(string text) =>
        int.TryParse(text, out int value) ? value : Error.Validation("number.invalid", "Sayı değil.", "text");

    [Fact]
    public void Value_and_error_convert_implicitly()
    {
        Result<int> success = 42;
        Result<int> failure = NotFound;

        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.Empty(success.Errors);

        Assert.True(failure.IsFailure);
        Assert.Equal(NotFound, failure.FirstError);
        Assert.Single(failure.Errors);
    }

    [Fact]
    public void Reading_value_of_failure_throws_instead_of_returning_default()
    {
        Result<string> failure = NotFound;

        Assert.Throws<InvalidOperationException>(() => failure.Value);
        Assert.Null(failure.ValueOrDefault);
        Assert.False(failure.TryGetValue(out _));
    }

    [Fact]
    public void Default_result_is_a_failure_not_a_silent_success()
    {
        Result<int> result = default;

        Assert.True(result.IsFailure);
        Assert.Equal("result.uninitialized", result.FirstError.Code);
    }

    [Fact]
    public void Failure_requires_at_least_one_error()
    {
        Assert.Throws<ArgumentException>(() => Result<int>.Failure(Array.Empty<Error>()));
    }

    [Fact]
    public void Then_and_map_stop_at_first_error()
    {
        int calls = 0;

        Result<string> ok = Parse("10").Then(v => v < 5 ? TooCheap : Result.Ok(v * 2)).Map(v => $"#{v}");
        Result<string> failed = Parse("abc").Map(v => { calls++; return v; }).Map(v => $"#{v}");

        Assert.Equal("#20", ok.Value);
        Assert.Equal("number.invalid", failed.FirstError.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Ensure_turns_unmet_condition_into_error()
    {
        Assert.Equal(TooCheap, Parse("3").Ensure(v => v >= 5, TooCheap).FirstError);
        Assert.Equal(7, Parse("7").Ensure(v => v >= 5, TooCheap).Value);
    }

    [Fact]
    public void Validate_collects_all_errors_and_skips_passed_checks()
    {
        Result<Success> none = Result.Validate(null, null);
        Result<Success> two = Result.Validate(NotFound, null, TooCheap);

        Assert.True(none.IsSuccess);
        Assert.Equal(new[] { NotFound, TooCheap }, two.Errors.ToArray());
    }

    [Fact]
    public void Errors_of_one_result_can_be_returned_as_another_type()
    {
        static Result<string> Create(int price)
        {
            Result<Success> valid = Result.Validate(price < 5 ? TooCheap : null);
            if (valid.IsFailure)
                return valid.Errors;

            return $"ok:{price}";
        }

        Assert.Equal(TooCheap, Create(1).FirstError);
        Assert.Equal("ok:9", Create(9).Value);
    }

    [Fact]
    public void Combine_merges_errors_of_all_failed_results()
    {
        Result<Success> combined = Result.Combine(Parse("1"), Parse("x"), Result.Fail<string>(TooCheap));

        Assert.Equal(2, combined.Errors.Length);
        Assert.True(Result.Combine(Parse("1"), Parse("2")).IsSuccess);
    }

    [Fact]
    public void Match_else_and_success_conversion()
    {
        Assert.Equal("10", Parse("10").Match(v => v.ToString(), e => e[0].Code));
        Assert.Equal("number.invalid", Parse("x").Match(v => v.ToString(), e => e[0].Code));
        Assert.Equal(-1, Parse("x").Else(-1).Value);

        Result<Success> success = Result.Success;
        Assert.True(success.IsSuccess);
        Assert.True(Parse("5").ToSuccess().IsSuccess);
        Assert.False(Parse("x").ToSuccess().IsSuccess);
    }

    [Fact]
    public async Task Null_values_and_task_chains()
    {
        static Task<string?> Find(bool exists) => Task.FromResult<string?>(exists ? "kalem" : null);

        Result<int> found = await Find(true).ToResult(NotFound).Map(s => s.Length);
        Result<int> missing = await Find(false).ToResult(NotFound).Map(s => s.Length);
        Result<string> async = await Find(true)
            .ToResult(NotFound)
            .ThenAsync(s => Task.FromResult(Result.Ok(s.ToUpperInvariant())))
            .Ensure(s => s.Length > 2, TooCheap);

        Assert.Equal(5, found.Value);
        Assert.Equal(NotFound, missing.FirstError);
        Assert.Equal("KALEM", async.Value);
        Assert.Equal(NotFound, ((int?)null).ToResult(NotFound).FirstError);
    }

    [Fact]
    public void Throw_if_failure_is_for_impossible_failures()
    {
        Assert.Equal(1, Parse("1").ThrowIfFailure());
        ResultFailedException ex = Assert.Throws<ResultFailedException>(() => Parse("x").ThrowIfFailure());
        Assert.Equal("number.invalid", ex.Errors[0].Code);
    }

    [Fact]
    public void Error_equality_ignores_metadata_and_metadata_is_copied()
    {
        Error withStock = TooCheap.WithMetadata("stock", 3);

        Assert.Equal(TooCheap, withStock);
        Assert.Empty(TooCheap.Metadata);
        Assert.Equal(3, withStock.Metadata["stock"]);
        Assert.Equal("Name", Error.Validation("required", "Zorunlu.").ForField("Name").Field);
    }

    [Fact]
    public void Failure_can_be_created_for_unknown_response_type()
    {
        Assert.True(ResultTypes.TryCreateFailure([NotFound], out Result<List<int>> failure));
        Assert.Equal(NotFound, failure.FirstError);

        Assert.False(ResultTypes.TryCreateFailure([NotFound], out int _));
        Assert.True(ResultTypes.IsResult(typeof(Result<Success>)));
        Assert.False(ResultTypes.IsResult(typeof(string)));
    }
}

public class ResultJsonTests
{
    private sealed record Item(int Id, string Name);

    [Fact]
    public void Success_and_failure_round_trip_through_json()
    {
        Result<Item> success = new Item(1, "Kalem");
        Result<Item> failure = Result.Validate(Error.Validation("required", "Ad boş.", "Name"), Error.NotFound("x", "Yok.")).Errors;

        Result<Item> successCopy = System.Text.Json.JsonSerializer.Deserialize<Result<Item>>(System.Text.Json.JsonSerializer.Serialize(success));
        Result<Item> failureCopy = System.Text.Json.JsonSerializer.Deserialize<Result<Item>>(System.Text.Json.JsonSerializer.Serialize(failure));

        Assert.Equal(success.Value, successCopy.Value);
        Assert.Equal(failure.Errors.ToArray(), failureCopy.Errors.ToArray());
        Assert.Equal(ErrorType.NotFound, failureCopy.Errors[1].Type);
    }
}
