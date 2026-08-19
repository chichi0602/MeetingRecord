using MeetingRecord.AccessDatas.Models;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace MeetingRecord.Business.Helpers.Searchs;

public class CombinedSearchHelper
{
    #region Project 輔助方法

    /// <summary>
    /// 組合 Expression 條件 (AND)
    /// </summary>
    public static Expression<Func<Project, bool>> ProjectCombinePredicates(
        Expression<Func<Project, bool>> first,
        Expression<Func<Project, bool>> second)
    {
        var parameter = Expression.Parameter(typeof(Project), "p");

        var leftVisitor = new ReplaceExpressionVisitor(first.Parameters[0], parameter);
        var left = leftVisitor.Visit(first.Body)
            ?? throw new InvalidOperationException("Unable to compose the left search expression.");

        var rightVisitor = new ReplaceExpressionVisitor(second.Parameters[0], parameter);
        var right = rightVisitor.Visit(second.Body)
            ?? throw new InvalidOperationException("Unable to compose the right search expression.");

        return Expression.Lambda<Func<Project, bool>>(
            Expression.AndAlso(left, right), parameter);
    }

    /// <summary>
    /// 套用排序
    /// </summary>
    public static List<Project> ProjectApplySorting(List<Project> projects, string? sortBy, bool descending)
    {
        if (string.IsNullOrEmpty(sortBy))
        {
            return projects;
        }

        return sortBy.ToLower() switch
        {
            "title" => descending
                ? projects.OrderByDescending(p => p.Title).ToList()
                : projects.OrderBy(p => p.Title).ToList(),
            "startdate" => descending
                ? projects.OrderByDescending(p => p.StartDate).ToList()
                : projects.OrderBy(p => p.StartDate).ToList(),
            "enddate" => descending
                ? projects.OrderByDescending(p => p.EndDate).ToList()
                : projects.OrderBy(p => p.EndDate).ToList(),
            "status" => descending
                ? projects.OrderByDescending(p => p.Status).ToList()
                : projects.OrderBy(p => p.Status).ToList(),
            "priority" => descending
                ? projects.OrderByDescending(p => p.Priority).ToList()
                : projects.OrderBy(p => p.Priority).ToList(),
            "completionpercentage" => descending
                ? projects.OrderByDescending(p => p.CompletionPercentage).ToList()
                : projects.OrderBy(p => p.CompletionPercentage).ToList(),
            "createdat" => descending
                ? projects.OrderByDescending(p => p.CreatedAt).ToList()
                : projects.OrderBy(p => p.CreatedAt).ToList(),
            "updatedat" => descending
                ? projects.OrderByDescending(p => p.UpdatedAt).ToList()
                : projects.OrderBy(p => p.UpdatedAt).ToList(),
            _ => projects
        };
    }

    #endregion
}
