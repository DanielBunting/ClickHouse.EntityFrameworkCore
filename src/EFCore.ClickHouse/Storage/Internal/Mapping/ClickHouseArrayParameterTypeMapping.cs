using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClickHouse.EntityFrameworkCore.Storage.Internal.Mapping;

/// <summary>
/// An <c>Array(T)</c> mapping used only to bind a captured collection as a single query parameter —
/// the <c>has({p:Array(T)}, column)</c> rewrite in
/// <c>ClickHouseSqlNullabilityProcessor.VisitCollectionParameterIn</c>.
/// <para>
/// The captured collection can be any <see cref="IEnumerable"/> shape (<c>HashSet&lt;T&gt;</c>, a lazy
/// iterator, <c>long?[]</c>, a <c>List&lt;string?&gt;</c> containing nulls, …), but the driver's
/// parameter formatter only serializes arrays and <c>List&lt;T&gt;</c>, and throws on null elements.
/// This mapping normalizes the value into a null-free <c>T[]</c> at binding time — the only point in
/// the pipeline where the real collection is visible, since the nullability processor works purely on
/// expression placeholders and deliberately never reads parameter values (keeping the generated SQL
/// independent of the collection's contents).
/// </para>
/// <para>
/// Stripping null elements preserves .NET <c>Contains</c> semantics: the rewrite only fires when the
/// tested item is non-nullable, and a null element can never equal a non-null item — for both
/// <c>Contains</c> and negated <c>Contains</c>. It is the same normalization EF Core's per-element
/// expansion performs for a non-nullable item.
/// </para>
/// </summary>
public class ClickHouseArrayParameterTypeMapping : ClickHouseArrayTypeMapping
{
    private readonly Type _elementType;

    public ClickHouseArrayParameterTypeMapping(RelationalTypeMapping elementMapping)
        : base(elementMapping)
    {
        _elementType = Nullable.GetUnderlyingType(elementMapping.ClrType) ?? elementMapping.ClrType;
    }

    protected ClickHouseArrayParameterTypeMapping(RelationalTypeMappingParameters parameters, RelationalTypeMapping elementMapping)
        : base(parameters, elementMapping)
    {
        _elementType = Nullable.GetUnderlyingType(elementMapping.ClrType) ?? elementMapping.ClrType;
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new ClickHouseArrayParameterTypeMapping(parameters, ElementMapping);

    public override DbParameter CreateParameter(
        DbCommand command,
        string name,
        object? value,
        bool? nullable = null,
        ParameterDirection direction = ParameterDirection.Input)
        => base.CreateParameter(command, name, NormalizeCollectionValue(value), nullable, direction);

    private object? NormalizeCollectionValue(object? value)
    {
        if (value is not IEnumerable source || value is string)
        {
            return value;
        }

        // Fast path: a T[] of a non-nullable value type can neither contain nulls nor need
        // reshaping, so the common captured-array case binds zero-copy. Reference-type arrays
        // (string[]) still take the scan below because their elements can be null.
        if (_elementType.IsValueType && value.GetType() == _elementType.MakeArrayType())
        {
            return value;
        }

        var elements = new List<object>();
        foreach (var element in source)
        {
            if (element is not null)
            {
                elements.Add(element);
            }
        }

        // Array.CreateInstance over the non-nullable element type: boxed T? values unbox into a T[]
        // slot, and reference-type elements are already null-free after the filter above.
        var array = Array.CreateInstance(_elementType, elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            array.SetValue(elements[i], i);
        }

        return array;
    }
}
