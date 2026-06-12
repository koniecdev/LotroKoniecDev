using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Options;

public static class OptionsBuilderExtensions
{
    extension<TOptions>(OptionsBuilder<TOptions> optionsBuilder) where TOptions : class
    {
        public OptionsBuilder<TOptions> ValidateFluentValidation()
        {
            optionsBuilder.Services.AddSingleton<IValidateOptions<TOptions>>(serviceProvider =>
                new FluentValidateOptions<TOptions>(
                    serviceProvider,
                    optionsBuilder.Name));

            return optionsBuilder;
        }
    }
}
