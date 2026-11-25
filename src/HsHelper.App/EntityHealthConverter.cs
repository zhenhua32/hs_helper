using System.Globalization;
using System.Windows.Data;
using HsHelper.Core;

namespace HsHelper.App;

public sealed class EntityHealthConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [GameSnapshot snapshot, int entityId] ? snapshot.GetRemainingHealth(entityId) : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
