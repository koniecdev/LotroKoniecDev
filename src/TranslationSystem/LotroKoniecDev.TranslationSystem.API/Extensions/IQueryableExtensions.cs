using System.Linq.Expressions;
using LotroKoniecDev.TranslationSystem.API.QueriesSorting;

namespace LotroKoniecDev.TranslationSystem.API.Extensions;

internal static class IQueryableExtensions
{
    extension<TAggregate>(IQueryable<TAggregate> query)
    {
        public IQueryable<TAggregate> ApplyPagination(int page, int pageSize)
        {
            query = query
                .Skip((page - 1) * pageSize)
                .Take(pageSize);

            return query;
        }

        /// <summary>
        /// Orders <paramref name="query"/> by the keys from the user's <c>?sort=</c>, and always adds
        /// the unique <paramref name="tiebreaker"/> at the end. That last unique key is required: a
        /// paged list ordered only by keys that can repeat has no defined order for equal rows, so
        /// PostgreSQL may return a different order per page and quietly show a row twice on one page and
        /// not at all on another.
        /// When the sort is empty or malformed, the tiebreaker becomes the only order, so the result is
        /// always fully ordered. The direction of a unique key does not matter, so tiebreakers always
        /// ascend.
        /// </summary>
        public IQueryable<TAggregate> ApplyMultipleSorting(
            string sort,
            Func<string, Expression<Func<TAggregate, object>>> propertySelector,
            Expression<Func<TAggregate, object>> tiebreaker,
            params Expression<Func<TAggregate, object>>[] additionalTiebreakers)
        {
            List<SortItem> sortItems = SortParser.Parse(sort).ToList();

            for (int i = 0; i < sortItems.Count; i++)
            {
                SortItem sortItem = sortItems[i];
                Expression<Func<TAggregate, object>> sortExpression = propertySelector(sortItem.PropertyName);

                query = i == 0
                    ? query.ApplySorting(sortExpression, sortItem.Operand is SortOperand.Asc)
                    : query.ApplyThenSorting(sortExpression, sortItem.Operand is SortOperand.Asc);
            }

            foreach (Expression<Func<TAggregate, object>> uniqueKey in additionalTiebreakers.Prepend(tiebreaker))
            {
                query = query is IOrderedQueryable<TAggregate> orderedQuery
                    ? orderedQuery.ThenBy(uniqueKey)
                    : query.OrderBy(uniqueKey);
            }

            return query;
        }

        private IQueryable<TAggregate> ApplySorting(
            Expression<Func<TAggregate, object>>? orderByExpression,
            bool ascending)
        {
            if (orderByExpression is null)
            {
                return query;
            }

            return ascending
                ? query.OrderBy(orderByExpression)
                : query.OrderByDescending(orderByExpression);
        }

        private IQueryable<TAggregate> ApplyThenSorting(
            Expression<Func<TAggregate, object>>? orderByExpression,
            bool ascending)
        {
            if (orderByExpression is null || query is not IOrderedQueryable<TAggregate> orderedQueryable)
            {
                return query;
            }

            return ascending
                ? orderedQueryable.ThenBy(orderByExpression)
                : orderedQueryable.ThenByDescending(orderByExpression);
        }
    }
}
