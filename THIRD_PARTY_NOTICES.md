# Avisos de terceiros

O código próprio está sob MIT, conforme `LICENSE`. As dependências mantêm seus titulares e termos. Esta lista usa os pacotes efetivamente restaurados nos projetos locais; não inclui o SDK no repositório.

| Pacote | Versão | Licença declarada no pacote |
|---|---|---|
| Apache.Arrow | 23.0.0 | Apache-2.0 |
| Apache.Arrow.Scalars | 23.0.0 | Apache-2.0 |
| DbcToDbf | 1.0.4 | MIT |
| DuckDB.NET.Bindings.Full | 1.5.6.1 | MIT |
| DuckDB.NET.Data.Full | 1.5.6.1 | MIT |
| ExcelDataReader | 3.9.0 | MIT |
| FluentFTP | 55.0.0 | MIT |
| Humanizer.Core | 3.0.10 | MIT |
| Json.More.Net | 3.0.1 | OSMFEULA.txt |
| JsonPointer.Net | 7.0.3 | OSMFEULA.txt |
| JsonSchema.Net | 9.4.1 | OSMFEULA.txt |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT |
| Npgsql | 10.0.3 | PostgreSQL |
| SharpCompress | 0.50.7 | MIT |

## Condições preservadas

JsonSchema.Net, JsonPointer.Net e Json.More.Net distribuem os binários oficiais com OSMFEULA. Os textos originais estão em `licenses/third-party/`. A licença MIT deste projeto não substitui esses termos. Os pacotes atuais não foram recompilados a partir do código-fonte. A aceitação das dependências foi informada pelo titular; esta atualização não altera os binários.

A TUI usa Textual, declarado em seus requisitos (`>=1.0,<2.0`); a versão efetivamente instalada no servidor não foi coletada. A lista acima cobre NuGet, não constitui inventário fechado de uma futura distribuição Python/binária. Preserve também os avisos dos componentes transitivos e nativos da distribuição que vier a publicar.

Os arquivos de licença/avisos disponíveis nos pacotes locais foram copiados sem alterações. Pacotes que declaram apenas uma expressão SPDX devem conservar essa identificação e seus avisos na distribuição. O manifesto `licenses/third-party/manifest.json` registra os textos copiados.
