using System;
namespace AutoMapper
{
    public interface IMemberConfigurationExpression<TSource> { void MapFrom<TMember>(Func<TSource, TMember> source); }
    public class MappingExpression<TSource, TDestination>
    {
        public MappingExpression<TSource, TDestination> ForMember<TMember>(Func<TDestination, TMember> destination, Action<IMemberConfigurationExpression<TSource>> options) { return this; }
        public MappingExpression<TSource, TDestination> ConvertUsing<TConverter>() { return this; }
    }
    public class Profile
    {
        protected MappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>() { return new MappingExpression<TSource, TDestination>(); }
    }
}
