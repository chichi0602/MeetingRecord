using System.Linq.Expressions;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// Expression 參數替換訪問者
/// </summary>
public class ReplaceExpressionVisitor : ExpressionVisitor
{
    private readonly Expression _oldValue;
    private readonly Expression _newValue;

    public ReplaceExpressionVisitor(Expression oldValue, Expression newValue)
    {
        _oldValue = oldValue;
        _newValue = newValue;
    }

    public override Expression? Visit(Expression? node)
    {
        if (node == _oldValue)
        {
            return _newValue;
        }
        return base.Visit(node);
    }
}
