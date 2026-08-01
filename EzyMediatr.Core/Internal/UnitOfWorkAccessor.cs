using System.Threading;
using EzyMediatr.Core.Transactions;

namespace EzyMediatr.Core.Internal;

internal sealed class UnitOfWorkAccessor
{
    private readonly AsyncLocal<IUnitOfWork?> _unitOfWork = new();

    public IUnitOfWork? Current => _unitOfWork.Value;

    public IUnitOfWork? Push(IUnitOfWork unitOfWork)
    {
        var previous = _unitOfWork.Value;
        _unitOfWork.Value = unitOfWork;
        return previous;
    }

    public void Restore(IUnitOfWork? unitOfWork) => _unitOfWork.Value = unitOfWork;

    public ISqlUnitOfWork GetCurrentSql()
    {
        return _unitOfWork.Value as ISqlUnitOfWork
            ?? throw new InvalidOperationException("No active SQL unit of work is available for this request.");
    }
}
