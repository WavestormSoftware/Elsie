using Elsie.Routing;
using Xunit;

namespace Elsie.Tests;

public class RouteMatcherTests
{
    private sealed class SampleModule : ElsieModule
    {
        public SampleModule()
        {
            Get("/", () => ElsieResult.Text("root"));
            Get("/hello/{name}", ctx => ElsieResult.Text(ctx.RouteValues["name"]));
            Get("/items/{id:int}", ctx => ElsieResult.Text(ctx.RouteValues["id"]));
            Post("/items", () => ElsieResult.Status(201));
            Get("/files/{*path}", ctx => ElsieResult.Text(ctx.RouteValues["path"]));
            Get("/files/readme", () => ElsieResult.Text("readme"));
        }
    }

    private static RouteTable CreateTable() => RouteTable.FromModules([new SampleModule()]);

    [Fact]
    public void Matches_static_root()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.NotNull(match);
        Assert.Equal("GET", match!.Route.Method);
    }

    [Fact]
    public void Extracts_route_parameter()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/hello/Ada");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("Ada", match!.RouteValues["name"]);
    }

    [Fact]
    public void Int_constraint_accepts_digits()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/items/42");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("42", match!.RouteValues["id"]);
    }

    [Fact]
    public void Int_constraint_rejects_non_numeric()
    {
        var table = CreateTable();
        Assert.NotEqual(RouteLookupStatus.Matched, table.Lookup("GET", "/items/abc").Status);
    }

    [Fact]
    public void Method_mismatch_does_not_match()
    {
        var table = CreateTable();
        Assert.NotEqual(RouteLookupStatus.Matched, table.Lookup("GET", "/items").Status);
    }

    [Fact]
    public void Lookup_reports_method_not_allowed()
    {
        var table = CreateTable();
        var lookup = table.Lookup("GET", "/items");
        Assert.Equal(RouteLookupStatus.MethodNotAllowed, lookup.Status);
        Assert.Contains("POST", lookup.AllowedMethods);
    }

    [Fact]
    public void Catch_all_captures_remaining_segments()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/files/a/b/c");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("a/b/c", match!.RouteValues["path"]);
    }

    [Fact]
    public void Catch_all_allows_empty_remainder()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/files");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal(string.Empty, match!.RouteValues["path"]);
    }

    [Fact]
    public void Concrete_route_wins_over_catch_all()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("GET", "/files/readme");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("/files/readme", match!.Route.Template);
    }

    [Fact]
    public void Catch_all_not_final_throws_at_table_build()
    {
        var module = new BadCatchAllModule();
        Assert.Throws<InvalidOperationException>(() => RouteTable.FromModules([module]));
    }

    [Fact]
    public void Duplicate_routes_throw()
    {
        var module = new DupModule();
        Assert.Throws<InvalidOperationException>(() => RouteTable.FromModules([module]));
    }

    [Fact]
    public void Static_wins_over_param_regardless_of_registration_order()
    {
        var table = RouteTable.FromModules([new PrecedenceModuleParamFirst()]);
        var matchLookup = table.Lookup("GET", "/users/new");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("/users/new", match!.Route.Template);
    }

    [Fact]
    public void Constrained_param_wins_over_unconstrained()
    {
        var table = RouteTable.FromModules([new ConstrainedPrecedenceModule()]);
        var matchLookup = table.Lookup("GET", "/x/42");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("/x/{id:int}", match!.Route.Template);
    }

    [Fact]
    public void Ambiguous_same_precedence_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RouteTable.FromModules([new AmbiguousModule()]));
    }

    [Fact]
    public void Unknown_constraint_throws_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RouteTable.FromModules([new UnknownConstraintModule()]));
    }

    [Fact]
    public void Duplicate_parameter_name_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RouteTable.FromModules([new DupParamModule()]));
    }

    [Theory]
    [InlineData("alpha", "Ada", true)]
    [InlineData("alpha", "Ada1", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "yes", false)]
    [InlineData("guid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", true)]
    [InlineData("long", "9223372036854775807", true)]
    [InlineData("decimal", "1.5", true)]
    [InlineData("double", "3.14", true)]
    [InlineData("minlength(3)", "abc", true)]
    [InlineData("minlength(3)", "ab", false)]
    [InlineData("maxlength(2)", "ab", true)]
    [InlineData("maxlength(2)", "abc", false)]
    [InlineData("length(3)", "abc", true)]
    [InlineData("length(2,4)", "abc", true)]
    [InlineData("min(5)", "5", true)]
    [InlineData("min(5)", "4", false)]
    [InlineData("max(5)", "5", true)]
    [InlineData("max(5)", "6", false)]
    [InlineData("range(1,3)", "2", true)]
    [InlineData("range(1,3)", "4", false)]
    [InlineData("regex(^a+$)", "aaa", true)]
    [InlineData("regex(^a+$)", "ab", false)]
    public void Built_in_constraints(string constraint, string value, bool expected)
    {
        var module = new DynamicConstraintModule(constraint);
        var table = RouteTable.FromModules([module]);
        var matched = table.Lookup("GET", $"/c/{value}").Status == RouteLookupStatus.Matched;
        Assert.Equal(expected, matched);
    }

    [Fact]
    public void Custom_constraint_from_options()
    {
        var options = new ElsieOptions();
        options.RouteConstraints["slug"] = v => v.Length > 0 && v.All(static c => char.IsLetterOrDigit(c) || c == '-');
        var table = RouteTable.FromModules([new CustomConstraintModule()], options);
        Assert.Equal(RouteLookupStatus.Matched, table.Lookup("GET", "/p/hello-world").Status);
        Assert.NotEqual(RouteLookupStatus.Matched, table.Lookup("GET", "/p/hello_world").Status);
    }

    [Fact]
    public void Optional_parameter_matches_with_and_without_value()
    {
        var table = RouteTable.FromModules([new OptionalModule()]);
        var bareLookup = table.Lookup("GET", "/page");
        Assert.Equal(RouteLookupStatus.Matched, bareLookup.Status);
        var bare = bareLookup.Match;
        Assert.False(bare!.RouteValues.ContainsKey("n"));

        var withLookup = table.Lookup("GET", "/page/3");
        Assert.Equal(RouteLookupStatus.Matched, withLookup.Status);
        var with = withLookup.Match;
        Assert.Equal("3", with!.RouteValues["n"]);
    }

    [Fact]
    public void Default_parameter_fills_when_absent()
    {
        var table = RouteTable.FromModules([new DefaultModule()]);
        var bareLookup = table.Lookup("GET", "/take");
        Assert.Equal(RouteLookupStatus.Matched, bareLookup.Status);
        var bare = bareLookup.Match;
        Assert.Equal("10", bare!.RouteValues["n"]);

        var withLookup = table.Lookup("GET", "/take/5");
        Assert.Equal(RouteLookupStatus.Matched, withLookup.Status);
        var with = withLookup.Match;
        Assert.Equal("5", with!.RouteValues["n"]);
    }

    [Fact]
    public void Implicit_head_falls_back_to_get()
    {
        var table = CreateTable();
        var matchLookup = table.Lookup("HEAD", "/hello/Ada");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("GET", match!.Route.Method);
    }

    [Fact]
    public void Implicit_head_can_be_disabled()
    {
        var options = new ElsieOptions { ImplicitHead = false };
        var table = RouteTable.FromModules([new SampleModule()], options);
        var lookup = table.Lookup("HEAD", "/hello/Ada");
        Assert.Equal(RouteLookupStatus.MethodNotAllowed, lookup.Status);
        Assert.Contains("GET", lookup.AllowedMethods);
    }

    [Fact]
    public void Named_route_and_link_generation()
    {
        var table = RouteTable.FromModules([new NamedModule()]);
        Assert.Equal("/todos/42", table.GetPathByName("getTodo", new Dictionary<string, string?> { ["id"] = "42" }));
        Assert.Equal("/todos/7", table.GetPathByName("getTodo", new { id = 7 }));
    }

    [Fact]
    public void Duplicate_route_names_throw()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RouteTable.FromModules([new DupNameModule()]));
    }

    [Fact]
    public void Datetime_constraint_accepts_iso()
    {
        var table = RouteTable.FromModules([new DatetimeModule()]);
        Assert.Equal(RouteLookupStatus.Matched, table.Lookup("GET", "/d/2024-01-02T03:04:05Z").Status);
    }

    private sealed class DupModule : ElsieModule
    {
        public DupModule()
        {
            Get("/x", () => ElsieResult.Text("a"));
            Get("/x", () => ElsieResult.Text("b"));
        }
    }

    private sealed class BadCatchAllModule : ElsieModule
    {
        public BadCatchAllModule()
        {
            Get("/{*path}/tail", ctx => ElsieResult.Text(ctx.RouteValues["path"]));
        }
    }

    private sealed class PrecedenceModuleParamFirst : ElsieModule
    {
        public PrecedenceModuleParamFirst()
        {
            Get("/users/{id}", () => ElsieResult.Text("param"));
            Get("/users/new", () => ElsieResult.Text("static"));
        }
    }

    private sealed class ConstrainedPrecedenceModule : ElsieModule
    {
        public ConstrainedPrecedenceModule()
        {
            Get("/x/{id}", () => ElsieResult.Text("any"));
            Get("/x/{id:int}", () => ElsieResult.Text("int"));
        }
    }

    private sealed class AmbiguousModule : ElsieModule
    {
        public AmbiguousModule()
        {
            Get("/users/{id}", () => ElsieResult.Text("id"));
            Get("/users/{name}", () => ElsieResult.Text("name"));
        }
    }

    private sealed class UnknownConstraintModule : ElsieModule
    {
        public UnknownConstraintModule()
        {
            Get("/x/{id:nope}", () => ElsieResult.Text("x"));
        }
    }

    private sealed class DupParamModule : ElsieModule
    {
        public DupParamModule()
        {
            Get("/{id}/{id}", () => ElsieResult.Text("x"));
        }
    }

    private sealed class DynamicConstraintModule : ElsieModule
    {
        public DynamicConstraintModule(string constraint)
        {
            Get($"/c/{{v:{constraint}}}", ctx => ElsieResult.Text(ctx.RouteValues["v"]));
        }
    }

    private sealed class CustomConstraintModule : ElsieModule
    {
        public CustomConstraintModule()
        {
            Get("/p/{s:slug}", ctx => ElsieResult.Text(ctx.RouteValues["s"]));
        }
    }

    private sealed class OptionalModule : ElsieModule
    {
        public OptionalModule()
        {
            Get("/page/{n?}", ctx => ElsieResult.Text(ctx.RouteOrDefault("n") ?? "none"));
        }
    }

    private sealed class DefaultModule : ElsieModule
    {
        public DefaultModule()
        {
            Get("/take/{n=10}", ctx => ElsieResult.Text(ctx.RouteValues["n"]));
        }
    }

    private sealed class NamedModule : ElsieModule
    {
        public NamedModule()
        {
            Get("/todos/{id}", ctx => ElsieResult.Text(ctx.RouteValues["id"])).Named("getTodo");
        }
    }

    private sealed class DupNameModule : ElsieModule
    {
        public DupNameModule()
        {
            Get("/a", () => ElsieResult.Text("a")).Named("same");
            Get("/b", () => ElsieResult.Text("b")).Named("same");
        }
    }

    private sealed class DatetimeModule : ElsieModule
    {
        public DatetimeModule()
        {
            Get("/d/{when:datetime}", ctx => ElsieResult.Text(ctx.RouteValues["when"]));
        }
    }
}
