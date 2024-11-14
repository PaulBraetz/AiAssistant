namespace AiAssistant;

using Microsoft.EntityFrameworkCore;

public class Functions(FunctionsDbContext dbContext)
{
    public async Task<Boolean> Insert(FunctionEntity functionEntity, CancellationToken ct)
    {
        if(await dbContext.FunctionEntities.ContainsAsync(functionEntity, ct))
            return false;
        _ = await dbContext.FunctionEntities.AddAsync(functionEntity, ct);
        _ = await dbContext.SaveChangesAsync(ct);
        return true;
    }
    public async Task<Boolean> Delete(String functionName, CancellationToken ct)
    {
        if(await dbContext.FunctionEntities.FindAsync(functionName, ct) is not { } f)
            return false;

        _ = dbContext.FunctionEntities.Remove(f);
        _ = dbContext.SaveChangesAsync(ct);
        return true;
    }
    public async Task<FunctionEntity[]> GetAll(CancellationToken ct) => await dbContext.FunctionEntities.ToArrayAsync(ct);
}
