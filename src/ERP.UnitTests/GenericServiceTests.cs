using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 通用 CRUD 服务测试
/// </summary>
public class GenericServiceTests
{
    [Fact]
    public async Task CreateAsync_新增实体_可查询到()
    {
        using var db = TestDbFactory.Create();
        var service = new GenericService<BaseCustomer>(db);

        var entity = await service.CreateAsync(new BaseCustomer
        {
            CustomerCode = "C001",
            CustomerName = "测试客户",
            Status = 1
        });

        Assert.True(entity.Id > 0);
        var found = await service.GetByIdAsync(entity.Id);
        Assert.Equal("C001", found.CustomerCode);
    }

    [Fact]
    public async Task UpdateAsync_更新实体_字段被修改()
    {
        using var db = TestDbFactory.Create();
        var service = new GenericService<BaseCustomer>(db);
        var entity = await service.CreateAsync(new BaseCustomer
        {
            CustomerCode = "C001",
            CustomerName = "原始名称",
            Status = 1
        });

        entity.CustomerName = "新名称";
        await service.UpdateAsync(entity);

        var found = await service.GetByIdAsync(entity.Id);
        Assert.Equal("新名称", found.CustomerName);
    }

    [Fact]
    public async Task DeleteAsync_软删除_查询不到()
    {
        using var db = TestDbFactory.Create();
        var service = new GenericService<BaseCustomer>(db);
        var entity = await service.CreateAsync(new BaseCustomer
        {
            CustomerCode = "C001",
            CustomerName = "待删除客户",
            Status = 1
        });

        await service.DeleteAsync(entity.Id);

        await Assert.ThrowsAsync<BusinessException>(() => service.GetByIdAsync(entity.Id));
    }

    [Fact]
    public async Task GetPagedAsync_分页查询_返回正确总数()
    {
        using var db = TestDbFactory.Create();
        var service = new GenericService<BaseCustomer>(db);
        for (var i = 1; i <= 25; i++)
        {
            await service.CreateAsync(new BaseCustomer
            {
                CustomerCode = $"C{i:000}",
                CustomerName = $"客户{i}",
                Status = 1
            });
        }

        var result = await service.GetPagedAsync(new PageQuery { Page = 1, PageSize = 10 });

        Assert.Equal(25, result.Total);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(3, result.TotalPages);
    }
}
