using System.Text.RegularExpressions;
using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

public sealed class ResolverSqlTests
{
    [Fact]
    public void EveryFactAggregateUsesDeduplicatingVersionedNodeMembership()
    {
        string sql = FabricStatementQuery.ComponentsSql;
        Assert.Equal(5, Regex.Matches(sql, @"EXISTS \(SELECT 1 FROM dbo\.resolver_scope s").Count);
        Assert.Equal(5, Regex.Matches(sql, @"s\.catalog_version = @version AND s\.node_id = @node").Count);
        Assert.Equal(5, Regex.Matches(sql,
            @"s\.region_code = [fkhd]\.region_code AND s\.department_code = [fkhd]\.department_code").Count);
        Assert.DoesNotContain("@all", sql);
        Assert.DoesNotContain("@kind", sql);
        Assert.DoesNotContain("@code", sql);
        Assert.DoesNotContain("JOIN dbo.resolver_scope", sql);
        Assert.Contains("COUNT(*) FROM dbo.resolver_release", sql);
        Assert.Contains("catalog_version = @version", sql);
        Assert.Contains("search_index = @searchIndex", sql);
        Assert.Contains("embedding_deployment = @embeddingDeployment", sql);
        Assert.Contains("embedding_dimensions = @embeddingDimensions", sql);
    }

    [Fact]
    public void OrganizationMetadataRequiresCallerVisibleFactsNotOnlyPublicDimensions()
    {
        string sql = FabricStatementQuery.CatalogSql;
        Assert.Contains("INNER JOIN fact_finance_monthly f", sql);
        Assert.Contains("s.node_id = c.entity_id", sql);
        Assert.Contains("s.catalog_version = c.catalog_version", sql);
        Assert.Contains("f.region_code = s.region_code AND f.department_code = s.department_code", sql);
        Assert.DoesNotContain("TOP", sql);
        Assert.DoesNotContain("LIKE", sql);
        Assert.DoesNotContain("dim_region", sql);
        Assert.DoesNotContain("dim_department", sql);
    }

    [Fact]
    public void CurrentPriorAndSemiAdditivePeriodsRemainUnchanged()
    {
        string sql = FabricStatementQuery.ComponentsSql;
        Assert.Contains("DECLARE @closing date = DATEADD(month, -1, @end)", sql);
        Assert.Contains("h.month_start = @closing", sql);
        Assert.Contains("f.month_start >= @start AND f.month_start < @end", sql);
        Assert.Contains("k.month_start >= DATEADD(year, -1, @start)", sql);
        Assert.Contains("SUM(d.kpi_value * ISNULL(r.net_revenue, 0))", sql);
    }
}
