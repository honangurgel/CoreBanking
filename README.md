# CoreBanking

Core bancário didático para portfólio, desenvolvido com .NET e utilizando exclusivamente dados e movimentações fictícios. O projeto simula operações financeiras e integrações externas para demonstrar conceitos de arquitetura, consistência e processamento assíncrono.

> **Aviso:** este projeto não é uma instituição financeira, não movimenta dinheiro real e não se conecta ao Banco Central do Brasil (BACEN), ao Pix ou a qualquer instituição financeira. A integração descrita como BACEN é um simulador local.

## Objetivos

- Implementar operações bancárias simuladas com regras explícitas e trilha de auditoria.
- Demonstrar um ledger de dupla entrada, idempotência e consistência transacional.
- Simular integrações externas resilientes e processamento assíncrono.
- Exercitar testes, observabilidade, documentação e execução local com ferramentas gratuitas e open source.

## Produtos e escopo

### Primeira fase

- Clientes fictícios e abertura de contas.
- Consulta de saldo e extrato.
- Depósitos e saques simulados.
- Transferências entre contas do sistema.
- Pix simulado, com chaves fictícias, limites e comprovantes.
- Agendamento e cancelamento de operações, quando permitido pelo estado.
- Comunicação simulada com serviços externos para consultas, comunicações, liquidação e reporte.

### Evoluções planejadas

- Cartões virtuais de débito e crédito, autorizações e faturas.
- Empréstimos fictícios, parcelas e pagamentos.
- Poupança e simulação de rendimentos.
- Seguros simulados.

Os produtos são referências de domínio comuns no varejo bancário brasileiro; não representam ofertas, regras ou serviços de uma instituição específica.

## Arquitetura técnica

A primeira versão será um **monólito modular**, com API e workers que podem ser executados como processos separados. Essa abordagem mantém o desenvolvimento e a execução local simples, enquanto permite escalar os workers independentemente. A separação em microserviços só será considerada se houver uma necessidade concreta.

```text
Aplicações cliente
        |
        v
ASP.NET Core API
  |          \
  |           \-- Outbox transacional
  |                    |
  v                    v
PostgreSQL        Broker de mensagens
                       |
                       v
                Workers de integração
                       |
                       v
                Simulador BACEN
                       |
                       v
                 Inbox / estados
```

### Stack inicial

| Componente | Tecnologia planejada |
| --- | --- |
| Linguagem e runtime | C# / .NET 10 LTS |
| API HTTP | ASP.NET Core |
| Persistência relacional | PostgreSQL 18 |
| Acesso a dados | Entity Framework Core e provedor Npgsql compatível |
| Ambiente local | Docker Compose |
| Mensageria | RabbitMQ, executado localmente em container |
| Automação | GitHub Actions |

As versões de dependências são registradas nos arquivos do projeto e devem ser atualizadas conforme compatibilidade e suporte. Clientes, contas, depósitos, transferências internas e Pix fictícios têm implementação inicial.

### Módulos de domínio

- **Clientes:** cadastro de personagens fictícios e dados de demonstração.
- **Contas:** abertura, estado, titularidade e consulta.
- **Ledger:** lançamentos contábeis e projeções de saldo.
- **Pagamentos:** transferências internas e Pix simulado.
- **Integração:** adaptador do simulador externo, limites de taxa e processamento assíncrono.
- **Cartões e crédito:** funcionalidades planejadas para fases posteriores.
- **Auditoria:** histórico de transições e resultados relevantes.

Os módulos terão limites de responsabilidade explícitos. A implementação poderá organizá-los em projetos .NET separados dentro de uma única solução, sem introduzir chamadas de rede entre módulos locais.

## Fluxo de integração assíncrona

1. A API valida a requisição e sua chave de idempotência.
2. Em uma transação do PostgreSQL, registra a operação e o evento correspondente na **Outbox**.
3. Retorna um identificador de operação e o estado inicial, sem aguardar o simulador externo.
4. Um worker publica ou consome o evento e chama o adaptador do simulador, respeitando limites configurados.
5. A resposta é registrada de forma idempotente por meio de uma **Inbox**, e a operação avança para seu estado final ou para tratamento de falha.
6. O cliente consulta o estado pelo identificador da operação.

Estados de referência:

```text
Received -> Queued -> Processing -> Completed
                                \-> Failed
                                \-> RequiresReview
```

Uma operação que dependa de confirmação externa não será apresentada como concluída antes dessa confirmação. Tentativas repetidas deverão ser seguras: a entrega de mensagens poderá ocorrer mais de uma vez, e consumidores idempotentes evitarão efeitos duplicados. Falhas temporárias terão novas tentativas com atraso progressivo; mensagens que excederem a política serão encaminhadas para uma fila de falhas para inspeção e reprocessamento controlado.

## Regras financeiras e consistência

- Valores monetários serão representados em unidades menores inteiras (por exemplo, centavos para BRL), sem `float` ou `double`.
- Cada lançamento contábil terá ao menos duas partidas e deverá fechar em zero por moeda.
- Lançamentos confirmados serão imutáveis. Correções serão representadas por lançamentos compensatórios, não pela edição do histórico.
- Saldo será derivado do ledger ou mantido como projeção atualizável na mesma transação que registra os lançamentos.
- Operações concorrentes deverão preservar os invariantes financeiros no banco de dados.
- Chaves de idempotência terão escopo e política de retenção definidos; requisições repetidas não poderão executar a mesma operação financeira duas vezes.
- A trilha de auditoria registrará identificadores, estados e horários sem incluir segredos ou dados pessoais reais.

### Ledger e movimentações fictícias

O componente interno de ledger utiliza contas contábeis vinculadas a contas de clientes ou destinadas a contrapartidas do sistema. Cada partida usa centavos em BRL: valores positivos aumentam o saldo e negativos o diminuem. Uma conta de cliente não pode ficar negativa; uma conta de contrapartida pode.

O lançamento exige ao menos duas contas distintas, valores não nulos e soma zero. A migration adiciona proteções no PostgreSQL para rejeitar lançamentos incompletos ou desbalanceados, alterações de histórico e atualizações diretas de saldo. A confirmação atualiza as projeções de saldo na mesma transação, com bloqueio das contas em ordem determinística. Um estorno contém as partidas opostas ao lançamento original e não pode ser aplicado duas vezes ao mesmo lançamento.

Uma conta contábil de saldo zero é criada na mesma transação da conta de cliente; uma migration cria contas contábeis para contas já existentes. A API consulta a projeção e permite depósitos e transferências internos fictícios. Depósitos utilizam uma conta interna de contrapartida. Transferências com saldo insuficiente são registradas como rejeitadas, sem lançamentos.

Depósitos e transferências exigem o cabeçalho `Idempotency-Key` (até 128 caracteres, sem espaços nas extremidades). A chave é única por tipo de operação e é mantida sem expiração. Repetir dados idênticos retorna o mesmo resultado; repetir a chave com dados diferentes retorna `409`. Uma transferência rejeitada por saldo insuficiente mantém sua rejeição: uma nova tentativa exige outra chave. Contas inexistentes e entradas inválidas não reservam a chave.

Uma transferência concluída grava o evento na Outbox junto com o lançamento e a operação. O worker publica eventos persistentes com confirmação no RabbitMQ; um consumidor registra a Inbox antes de confirmar a mensagem, deduplicando entregas. Falhas de processamento explicitamente sinalizadas recebem até três tentativas por sequência de entrega, com atrasos de 1 e 2 segundos em filas quorum; após a terceira falha, a mensagem vai para `corebanking.transfers.failed`. Mensagens inválidas também vão diretamente para essa fila. Em falhas de infraestrutura, o worker registra o erro e tenta reconectar com atraso progressivo, sem consumir o limite de falhas de processamento.

Como a entrega é pelo menos uma vez, uma interrupção entre a publicação e a confirmação pode gerar cópias e mais tentativas totais do que o limite por sequência. Erros inesperados interrompem o worker, em vez de serem tratados silenciosamente. O consumidor atual apenas registra a Inbox: futuros efeitos de negócio deverão ser gravados atomicamente com ela. Isso não representa comunicação ou liquidação com o BACEN.

## Escala e metas de carga

O cenário de referência para o **simulador de integração** é de até **10.000 requisições por minuto** (aproximadamente 167 por segundo em média). Isso é uma meta de projeto e de teste, não uma capacidade já medida ou garantida. O plano de carga deverá incluir picos acima da média, atrasos do simulador, falhas, mensagens duplicadas, concorrência por conta e recuperação após indisponibilidade.

O sistema deverá observar, no mínimo:

- taxa de entrada e saída;
- tamanho e idade da mensagem mais antiga na fila;
- latência por operação e por integração;
- erros, tentativas, mensagens em fila de falhas e reprocessamentos;
- divergências entre ledger e projeções de saldo.

Aumentar o número de workers não deve permitir exceder o limite configurado para o serviço externo simulado. Nenhum número de instâncias ou meta de latência será tratado como garantia até que haja testes de carga reproduzíveis.

## Segurança e dados fictícios

- Utilizar apenas dados sintéticos gerados para desenvolvimento e demonstração.
- Não inserir credenciais, tokens, chaves ou connection strings com segredos no repositório.
- Configurações locais sensíveis devem ser fornecidas por variáveis de ambiente ou user secrets.
- Não usar CPFs, chaves Pix ou dados de cartões de pessoas reais. Dados de exemplo devem ser claramente identificados como fictícios e não devem ser usados para validar identidades reais.
- O simulador externo não fará chamadas de rede ao BACEN nem a participantes do Pix.

Os perfis HTTP e as portas do Compose usam loopback. Em `Development`, a configuração explícita `Authentication:Mode=Disabled` desativa a autenticação e rejeita conexões cujo endereço remoto não seja loopback; **não coloque um proxy na frente desse modo**, pois conexões encaminhadas podem parecer locais. Em outros ambientes, desativá-la é proibido e a API exige OIDC configurado. O simulador não tem autenticação. Mesmo com OIDC, não há isolamento por cliente, TLS obrigatório na API, nem garantias para produção: não exponha os serviços nem use dados reais. `/health` e `/health/ready` são anônimos; o último expõe o estado das dependências. O rate limit é local a cada instância.

Os valores padrão do Compose e exemplos no README são exclusivamente locais; alterá-los não torna o projeto adequado à produção. Arquivos `.env` e `.env.*` são ignorados pelo Git, exceto `.env.example`, que deve conter somente exemplos sem segredos. Revise logs e configurações antes de compartilhá-los, e não habilite o registro de dados sensíveis do EF Core. Não há política automatizada de retenção ou exclusão de dados fictícios; use uma base isolada para cada demonstração ou teste.

### Identidade e perfis operacionais

A API aceita access tokens JWT assinados com RS256 por um provedor OIDC, validando assinatura, emissor, audiência e validade. Requer a claim `typ` igual a `Bearer` e roles na claim multivalorada `roles`. Os perfis são independentes: atribua todos os necessários a cada identidade.

| Perfil | Permissões |
| --- | --- |
| `operador` | Cadastro de clientes, contas e chaves Pix; todas as consultas |
| `financeiro` | Criação de depósitos, transferências e operações Pix |
| `administrador` | Reprocessamento de operações Pix |

Sem token válido, as rotas protegidas respondem `401`; sem o perfil exigido, `403`. Os perfis não limitam o acesso a contas específicas e não substituem segregação entre clientes, auditoria de identidade, gestão de credenciais ou proteção contra abuso.

Para experimentar com Keycloak local e gratuito, defina credenciais temporárias em variáveis de ambiente e execute o perfil opcional:

```powershell
$env:KEYCLOAK_ADMIN_USER = '<usuario-admin-local>'
$env:KEYCLOAK_ADMIN_PASSWORD = '<senha-admin-local>'
docker compose --profile identity up -d keycloak
```

O Keycloak usa armazenamento de desenvolvimento efêmero: recriar ou remover o container perde a configuração. No console `http://localhost:8080`, crie um realm `corebanking`, os três realm roles acima e um client para obter access tokens. Configure mappers no client scope para incluir no **access token**: a audiência `corebanking-api` e os realm roles na claim multivalorada `roles` (não no ID token). Atribua ao usuário ou service account os roles apropriados e confirme as claims emitidas. Use Authorization Code com PKCE para clientes interativos; não salve segredos ou tokens no repositório.

No terminal da API, além das conexões já documentadas, selecione OIDC explicitamente:

```powershell
$env:Authentication__Mode = 'Oidc'
$env:Authentication__Authority = 'http://localhost:8080/realms/corebanking'
$env:Authentication__Audience = 'corebanking-api'
dotnet run --project .\src\CoreBanking.Api
```

Envie `Authorization: Bearer <access-token>` nas rotas protegidas. HTTP para discovery só é aceito em `Development` e para loopback; nos demais ambientes a autoridade deve ser HTTPS. O Keycloak `start-dev`, sua base efêmera e o modo sem autenticação servem apenas para testes locais. Reiniciar a API com outro modo requer alterar as variáveis e reiniciar o processo.

## Requisitos para desenvolvimento local

Após a implementação da estrutura inicial, o ambiente de desenvolvimento deverá ter:

- Windows, macOS ou Linux;
- .NET 10 SDK;
- Docker Engine/Desktop com Docker Compose;
- Git.

Verifique as ferramentas instaladas:

```powershell
dotnet --version
docker --version
docker compose version
git --version
```

### Executar localmente

Com o Docker Engine ativo, execute na raiz do repositório. Os comandos abaixo usam PowerShell:

```powershell
docker compose up -d
dotnet tool restore
dotnet restore
dotnet build

# Credenciais exclusivamente locais do Compose; substitua se configurou outras.
$env:ConnectionStrings__Banking = "Host=localhost;Port=5432;Database=corebanking;Username=corebanking;Password=corebanking_local"
dotnet ef database update --project .\src\CoreBanking.Infrastructure --startup-project .\src\CoreBanking.Infrastructure
# Necessários para o health check de prontidão da API.
$env:RabbitMQ__ConnectionString = "amqp://corebanking:corebanking_local@localhost:5672/"
$env:Pix__SimulatorUrl = "http://localhost:5272"
dotnet run --project .\src\CoreBanking.Api
```

O Compose publica as portas apenas em `localhost`. Para uso local, o PostgreSQL e o RabbitMQ usam por padrão o usuário `corebanking` e a senha `corebanking_local`; são credenciais exclusivas de desenvolvimento e não devem ser reutilizadas em outros ambientes. O painel local do RabbitMQ fica em `http://localhost:15672`.

Execute `dotnet test` em outro terminal: os testes de integração exigem Docker ativo e criam um PostgreSQL isolado e temporário. As migrations não são aplicadas automaticamente ao iniciar a API; execute o comando acima após atualizá-las. Para utilizar a API em um novo terminal, defina novamente `ConnectionStrings__Banking` antes de executar `dotnet run`.

O GitHub Actions também executa restore, build e testes em pushes e pull requests, usando um runner Linux com Docker. Isso permite validar o código no CI sem alterar a política de segurança do Windows, mas não garante que os executáveis serão aceitos localmente.

Para executar o worker em outro terminal, após aplicar as migrations:

```powershell
$env:ConnectionStrings__Banking = "Host=localhost;Port=5432;Database=corebanking;Username=corebanking;Password=corebanking_local"
$env:RabbitMQ__ConnectionString = "amqp://corebanking:corebanking_local@localhost:5672/"
dotnet run --project .\src\CoreBanking.Worker
```

Para inspecionar falhas, use o painel local do RabbitMQ e consulte logs e conteúdo antes de agir. Após corrigir a causa, execute o comando abaixo em outro terminal com `RabbitMQ__ConnectionString` configurada. Ele reenvia **uma mensagem do início** da fila de falhas e reinicia seu contador; não seleciona um ID específico nem altera o conteúdo. Mensagens inválidas retornarão à fila até serem corrigidas por procedimento apropriado. Repita apenas após verificar o resultado.

```powershell
dotnet run --project .\src\CoreBanking.Worker -- --reprocess-failed
```

Uma interrupção durante o reenvio pode produzir duplicatas; a Inbox evita registros duplicados. Não altere manualmente o ledger para reprocessar eventos.

### Simulador de liquidação

O projeto `CoreBanking.Simulator` fornece um contrato **fictício e independente** para desenvolver a integração. Execute-o localmente:

```powershell
dotnet run --project .\src\CoreBanking.Simulator --launch-profile http
```

Em `http://localhost:5272`, `GET /api/v1/institutions` apresenta um pequeno catálogo estático de nomes e ISPBs públicos. Os dados são referências, não representam conexão com as instituições e não são uma lista atualizada automaticamente. Fonte: [participantes do SPI — Banco Central](https://www.bcb.gov.br/content/estabilidadefinanceira/spi-pdf/participantes-spi-20261120.pdf).

`POST /api/v1/settlements` recebe `operationId` e `keyId` como UUIDs fictícios, `institutionIspb`, `direction` (`Outgoing` ou `Incoming`), `amountMinor`, `scenario` (`Confirm`, `Reject` ou `Unavailable`) e `delayMilliseconds` (0 a 10000). Para a mesma requisição, devolve a mesma decisão fictícia; `Unavailable` retorna HTTP 503. O serviço não guarda solicitações, não registra liquidações, não valida propriedade de chaves e não movimenta saldos reais. O worker consome suas respostas somente no fluxo fictício.

### Pix fictício

O fluxo inicial implementa envio e recebimento **exclusivamente simulados**. O limite é R$ 999.999.999.999.999,99 por operação (99.999.999.999.999.999 centavos). Uma chave local fictícia é um UUID criado por `POST /api/v1/pix/keys`, com `{"accountId":"..."}`. Para recebimento é necessário informar uma chave criada para a conta; no envio, o UUID representa uma chave externa fictícia e não é consultado em nenhum diretório real.

`POST /api/v1/pix/operations` exige `Idempotency-Key` (mesmas regras das transferências) e recebe `accountId`, `keyId`, `institutionIspb`, `direction` (`Outgoing` ou `Incoming`), `amountMinor` e `scenario` (`Confirm` ou `Reject`). Retorna `202` para operação aceita e o estado atual também nos replays; requisições com a mesma chave e dados diferentes retornam `409`. Saldo insuficiente é persistido e exige nova chave para tentar novamente. `GET /api/v1/pix/operations/{id}` consulta o estado; `GET /api/v1/pix/operations/{id}/receipt` traz os lançamentos e a trilha de estados, inclusive para operações não confirmadas.

No envio, o lançamento de reserva reduz o saldo disponível da conta imediatamente e credita uma conta contábil interna; confirmação transfere a reserva para compensação fictícia, enquanto rejeição devolve o valor à conta. No recebimento, o crédito só ocorre após confirmação. Cada mudança de estado e lançamento é persistida atomicamente. O próprio registro `Pending` funciona como fila durável no PostgreSQL, independente da Outbox de transferências internas; workers usam bloqueios `SKIP LOCKED` para evitar processamento simultâneo. A chamada ao simulador é determinística para a mesma operação e pode ser repetida após interrupção.

Configure o endereço **local** antes de iniciar o worker:

```powershell
$env:Pix__SimulatorUrl = "http://localhost:5272"
```

O worker aceita somente origem HTTP loopback e não segue redirecionamentos. Sem configuração, o processamento Pix fica desativado e as operações permanecem pendentes. Falhas temporárias do simulador são tentadas três vezes, com atrasos de 1 e 2 segundos. Após três falhas, ou resposta inválida, a operação passa a `RequiresReview` e a reserva permanece intacta. Depois de investigar e corrigir a causa, `POST /api/v1/pix/operations/{id}/retry` reinicia as tentativas mantendo os dados e o cenário originais; nunca libere a reserva manualmente por suposição. Uma indisponibilidade do PostgreSQL é tentada novamente pelo worker. As filas e as políticas Pix não usam a fila RabbitMQ de falhas de transferências.

As chamadas Pix ao simulador são limitadas globalmente por banco: 600 permissões por janela móvel de 60 segundos e 10 chamadas simultâneas por padrão. O limite é compartilhado por workers conectados ao mesmo PostgreSQL; operações que aguardam capacidade permanecem pendentes sem consumir tentativas. São permitidos picos até o limite, não há distribuição uniforme da taxa. A permissão é registrada antes da chamada e permanece contabilizada após falhas ou interrupções, mesmo se a chamada não ocorrer.

Os limites são valores de desenvolvimento, não uma capacidade medida. Para alterá-los, pare todos os workers, atualize a única linha de `"PixThrottleSettings"` no PostgreSQL (`"RequestsPerMinute"` de 1 a 100000; `"MaxConcurrent"` de 1 a 100) e reinicie todos com esta versão. Não execute versões antigas dos workers simultaneamente, pois elas não respeitam o limite. Os bloqueios de concorrência dependem da conexão PostgreSQL; interrupções abruptas e atrasos entre a concessão e a chamada não constituem uma garantia rígida de taxa ou concorrência no simulador. O acesso direto ao simulador não passa por este controle.

O simulador não persiste suas respostas nem representa BACEN, DICT ou liquidação real. Nesta demonstração a decisão é escolhida na requisição, sem autenticação, apenas para testes locais; não use o sistema para movimentação real.

`GET /health` verifica apenas se a API está em execução. `GET /health/ready` verifica conexão e consulta ao PostgreSQL, conexão autenticada ao RabbitMQ e resposta de saúde do simulador local. Configure as três variáveis no processo da API e inicie o simulador; a ausência ou indisponibilidade de qualquer dependência resulta em HTTP 503 e estado `Unhealthy`, incluindo quando o Pix está desativado no worker. O resultado contém apenas o estado geral e os estados `postgres`, `rabbitmq` e `simulator`, sem detalhes de conexão. A verificação não confirma migrations, filas, funcionamento dos workers ou capacidade de processamento. Cada consulta abre uma conexão ao broker: use frequência moderada e não exponha a API publicamente.

A API limita requisições por IP em janela fixa de um minuto, por instância, com padrão de 600 e resposta HTTP 429 sem fila. O valor pode ser alterado por `RateLimiting__PermitsPerMinute`; as rotas sob `/health` são isentas. Sem configuração confiável de proxy, o endereço observado pode ser compartilhado por vários clientes; não é um limite distribuído nem uma proteção de segurança.

### Logs e correlação de operações

API, worker e simulador emitem logs de console em JSON estruturado. Para acompanhar um Pix, busque `OperationId` (o `id` retornado pela API) nos logs da API, do worker e do simulador. Para transferências, busque `TransferId`; o worker também registra `MessageId` na publicação e no consumo, inclusive em falhas e reentregas. A API registra `RequestId` junto ao identificador retornado para vincular cada requisição à operação; replays idempotentes mantêm o mesmo identificador de operação. Reprocessamentos Pix registram se foram aceitos. Logs após processamento indicam o estado persistido, não garantem que uma linha foi emitida antes de uma interrupção.

Esta é uma correlação por identificador de negócio, não tracing distribuído de spans. Não são registrados corpos, chaves de idempotência ou valores nos eventos adicionados; configure e proteja também os logs do framework e da infraestrutura. O projeto não armazena logs em banco nem em buffers próprios: configure coleta e retenção externamente. As mensagens registram apenas campos pequenos e não serializam payloads inteiros; os resultados limitados de memória e vazão estão nos cenários de carga abaixo.

### Métricas operacionais

O worker publica instrumentos .NET `System.Diagnostics.Metrics` no meter `CoreBanking.Worker`. Um coletor externo compatível é necessário; nenhum endpoint ou armazenamento de métricas é iniciado automaticamente. Com `dotnet-counters` instalado, por exemplo, use `dotnet-counters monitor --process-id <PID-do-worker> --counters CoreBanking.Worker`. Para HTTP, o ASP.NET Core disponibiliza suas métricas nativas, incluindo `http.server.request.duration` no meter `Microsoft.AspNetCore.Hosting`.

| Instrumento | Significado |
| --- | --- |
| `corebanking.pix.processed` | Contagem de processamentos persistidos, por `status`; `Pending` indica nova tentativa agendada. |
| `corebanking.pix.throttled` | Seleções adiadas por limite de taxa ou concorrência, não operações únicas. |
| `corebanking.pix.simulator.duration` | Duração de cada chamada ao simulador, em segundos, inclusive falhas e cancelamentos. |
| `corebanking.transfer.messages` | Eventos por `outcome`: `published`, `acknowledged`, `retry`, `failed` ou `invalid`. |
| `corebanking.backlog.count` | Quantidade de Pix pendentes ou mensagens Outbox não publicadas, por `queue`. |
| `corebanking.backlog.oldest.age` | Idade em segundos do registro mais antigo dessas filas, ou zero se vazias. |
| `corebanking.rabbitmq.queue.ready` | Mensagens prontas em cada fila de transferências, sem incluir entregas ainda não confirmadas. |

As contagens são eventos por processo, não totais financeiros: reinícios, replays e falhas entre transação e emissão podem gerar perda ou duplicidade. Os indicadores de backlog consultam o PostgreSQL a cada 30 segundos somente enquanto houver um coletor inscrito; as filas RabbitMQ são consultadas no mesmo intervalo enquanto o worker estiver conectado e houver coletor. Erros são registrados e não produzem uma amostra de sucesso. As amostras são globais ao banco ou broker, portanto **não devem ser somadas entre workers**. Incluem Pix pendentes aguardando retry, mas não medem idade das mensagens RabbitMQ, operações em revisão, divergência do ledger nem disponibilidade do worker. Uma amostra antiga não é prova de saúde; monitore também o horário da coleta e a prontidão.

As tags têm valores limitados, sem identificadores, chaves ou payloads; não há buffer de eventos na aplicação. As consultas de backlog agregam dados no banco, porém podem gerar custo em bases grandes. As medições de LOH abaixo não isolam o custo das métricas.

### Primeiro fluxo de uso

Com a API em execução no perfil HTTP padrão (`http://localhost:5027`), crie um cliente e use o identificador retornado para abrir uma conta:

```powershell
$customer = Invoke-RestMethod -Method Post -Uri http://localhost:5027/api/v1/customers -ContentType "application/json" -Body '{"name":"Cliente Fictício"}'
$body = @{ customerId = $customer.id } | ConvertTo-Json
$account = Invoke-RestMethod -Method Post -Uri http://localhost:5027/api/v1/accounts -ContentType "application/json" -Body $body
Invoke-RestMethod -Uri "http://localhost:5027/api/v1/accounts/$($account.id)"
```

O nome deve ter de 1 a 200 caracteres após remover espaços nas extremidades. Cada conta é criada em estado `Active` para um cliente existente, com saldo inicial zero. É possível criar múltiplas contas por cliente; ainda não há fechamento ou bloqueio. Não existe autenticação: a API é para demonstração local, não para exposição pública.

Para criar dados sintéticos e creditá-los via ledger, com a API em execução:

```powershell
.\scripts\seed-demo.ps1 -Count 3
```

O script cria novos clientes e contas a cada execução, usa nomes aleatórios explicitamente fictícios e deposita 100.000 centavos por conta. Não apaga dados; se houver falha, os recursos já criados permanecem e seus IDs são exibidos. Não gera CPF, chaves Pix ou dados de cartões.

Para demonstrar Pix ponta a ponta, inicie API, simulador e worker com o mesmo PostgreSQL e RabbitMQ, conforme as instruções acima. Então execute `.\scripts\demo-pix.ps1`. O script aceita apenas endereço local, exige prontidão, cria uma nova conta fictícia, deposita 100.000 centavos, recebe 12.000 e envia 5.000; aguarda confirmações, verifica comprovantes e saldo final de 107.000 centavos. A conta e os registros permanecem no banco, inclusive em caso de falha; use um banco isolado quando não quiser persistir a demonstração.

### Cenário de carga isolado

O teste abaixo cria um PostgreSQL temporário por Testcontainers, executa transferências concorrentes entre duas contas fictícias e verifica idempotência, saldos, Outbox, partidas balanceadas e reconciliação das projeções. Não usa ou apaga o banco do Compose. O padrão é 40 operações; o valor pode ser alterado de 1 a 10.000. Execute-o separadamente para evitar interferência de outros testes:

```powershell
$env:COREBANKING_LOAD_OPERATIONS = '1000'
dotnet test .\tests\CoreBanking.IntegrationTests --filter 'FullyQualifiedName~LoadScenarioTests' --logger 'console;verbosity=detailed'
Remove-Item Env:\COREBANKING_LOAD_OPERATIONS
```

O cenário usa TestServer no próprio processo, até oito requisições concorrentes em ciclo fechado, uma única conta de origem e logs desativados; ajusta o limite da API para não bloquear a carga. Não mede rede HTTP real, worker, simulador, mensagens RabbitMQ, rejeições, distribuição de latência ou recuperação sob carga. A média por minuto é extrapolada do intervalo observado, não prova vazão sustentada. O tamanho do LOH exibido corresponde ao último GC observado do processo inteiro de teste, não ao pico nem exclusivamente à aplicação.

Referência local de 26/11/2026: Windows 10.0.26200, Intel i5-1334U, SDK .NET 10.0.101, Docker Engine 29.8.2, PostgreSQL 18 Alpine; 1.000 transferências em 18,95 segundos, média extrapolada de 3.167/min, LOH observado de 885.152 para 1.376.944 bytes. Não é uma garantia nem valida a meta de 10.000/min. A medição inclui o processo de teste e depende de GC, máquina e ambiente.

Para exercitar tráfego durante uma queda e recuperação do RabbitMQ, execute separadamente:

```powershell
$env:COREBANKING_RECOVERY_SECONDS = '60'
dotnet test .\tests\CoreBanking.IntegrationTests --filter 'FullyQualifiedName~RecoveryLoadTests' --logger 'console;verbosity=detailed'
Remove-Item Env:\COREBANKING_RECOVERY_SECONDS
```

Esse cenário usa containers temporários de PostgreSQL e RabbitMQ, HTTP loopback via Kestrel e um worker real no processo de teste. Quatro produtores em ciclo fechado aguardam 200 ms após cada resposta; o teste interrompe o RabbitMQ, confirma transferências e Outbox pendente durante a interrupção, reinicia o broker, continua o tráfego e aguarda a drenagem integral da Outbox para a Inbox. Verifica contagens, saldos e invariantes do ledger. A duração mínima do tráfego é configurável de 10 a 120 segundos; há teto de 5.000 operações e o limite de entrada é ajustado para 6.000/min. O processo de teste hospeda cliente, API e worker, com logs da API desativados e logger reduzido no worker. Não são medidos Pix, falhas do banco, percentis, pico do LOH ou capacidade de produção.

Na mesma máquina de referência, com build Debug, 60,18 segundos de tráfego geraram 1.041 transferências; 7 foram observadas durante a interrupção, e drenagem e validação levaram 9,57 segundos após o tráfego. O LOH após o último GC observado foi de 754.024 para 852.432 bytes no processo inteiro. São observações de uma execução limitada e não estabelecem taxa máxima ou vazão sustentada de 10.000/min.

### Carga Pix com processos separados

Execute os cenários isoladamente, com Docker ativo:

```powershell
$env:COREBANKING_PIX_LOAD_SECONDS = '60'
dotnet test .\tests\CoreBanking.IntegrationTests --filter 'FullyQualifiedName~PixProcessLoadTests' --logger 'console;verbosity=detailed'
Remove-Item Env:\COREBANKING_PIX_LOAD_SECONDS
```

API, worker e simulador rodam como processos locais separados; PostgreSQL e RabbitMQ são containers temporários. Em testes separados, o simulador ou o worker é interrompido e reiniciado durante tráfego de quatro produtores em ciclo fechado, com pausa de 400 ms. O teste comprova aceitação e pendência/falha persistida durante a interrupção, reprocessa operações em revisão e confere confirmações, saldos e ledger. A duração mínima é de 10 a 60 segundos e há teto de 1.000 operações. Os percentis são limites superiores aproximados de aceitação HTTP, não de liquidação.

Opcionalmente, instale `dotnet-counters` compatível com .NET 10 em um diretório de ferramentas local e defina `COREBANKING_DOTNET_COUNTERS` com o caminho absoluto do executável antes do comando acima. A coleta é por processo, a cada segundo, com duração de tráfego configurada mais 30 segundos, e falha se terminar antes do tráfego. Ela informa o maior `loh-size` amostrado para API e worker no cenário de falha do simulador; no reinício do worker mede apenas a API. Esses valores refletem o tamanho do LOH após GCs observados, podem incluir tempo após o tráfego e **não são o pico absoluto** nem medem o processo do simulador. A coleta temporária é removida ao fim.

Exemplo no Windows, com a versão usada na medição:

```powershell
dotnet tool install dotnet-counters --version 10.0.750501 --tool-path "$env:TEMP\corebanking-tools"
$env:COREBANKING_DOTNET_COUNTERS = "$env:TEMP\corebanking-tools\dotnet-counters.exe"
$env:COREBANKING_PIX_LOAD_SECONDS = '60'
dotnet test .\tests\CoreBanking.IntegrationTests --filter 'FullyQualifiedName~PixProcessLoadTests' --logger 'console;verbosity=detailed'
Remove-Item Env:\COREBANKING_DOTNET_COUNTERS, Env:\COREBANKING_PIX_LOAD_SECONDS
```

Referência local de 26/11/2026, mesma máquina acima, build Release:

- Falha do simulador: 530 Pix em 60,22 s, 212 durante a indisponibilidade; p50 ≤ 50 ms, p95 ≤ 50 ms, p99 ≤ 500 ms. Máximo amostrado após GC: 98.384 bytes na API e 98.384 no worker, com 90 amostras cada.
- Falha do worker: 553 Pix em 60,24 s, 8 durante a indisponibilidade; p50 ≤ 50 ms, p95 ≤ 50 ms, p99 ≤ 100 ms. Máximo amostrado após GC: 98.384 bytes na API, com 90 amostras; worker não medido durante o reinício.

São valores de uma execução por cenário, sem rede representativa; não comprovam 10.000 requisições/minuto.

## API

| Método | Endpoint | Finalidade |
| --- | --- | --- |
| `GET` | `/health` | Verificar se a API está em execução |
| `GET` | `/health/ready` | Verificar conectividade das dependências |
| `POST` | `/api/v1/customers` | Cadastrar cliente fictício |
| `GET` | `/api/v1/customers/{customerId}` | Consultar cliente |
| `POST` | `/api/v1/accounts` | Abrir conta |
| `GET` | `/api/v1/accounts/{accountId}` | Consultar conta e saldo em centavos (`balanceMinor`, moeda `BRL`) |
| `POST` | `/api/v1/deposits` | Depósito fictício; corpo `accountId`, `amountMinor` |
| `GET` | `/api/v1/deposits/{depositId}` | Consultar depósito |
| `POST` | `/api/v1/transfers` | Transferência interna; corpo `sourceAccountId`, `destinationAccountId`, `amountMinor` |
| `GET` | `/api/v1/transfers/{transferId}` | Consultar transferência |
| `POST` | `/api/v1/pix/keys` | Criar chave Pix fictícia |
| `GET` | `/api/v1/pix/keys/{id}` | Consultar chave Pix fictícia |
| `POST` | `/api/v1/pix/operations` | Criar operação Pix fictícia |
| `GET` | `/api/v1/pix/operations/{id}` | Consultar operação Pix |
| `GET` | `/api/v1/pix/operations/{id}/receipt` | Consultar comprovante Pix |
| `POST` | `/api/v1/pix/operations/{id}/retry` | Reprocessar operação em revisão |

Clientes, contas, depósitos e transferências criados retornam `201 Created`; operações Pix aceitas e reprocessamentos retornam `202 Accepted`. Entradas inválidas retornam `400`. A criação de cliente e conta ainda não implementa idempotência; extratos permanecem planejados.

## Testes

Os testes existentes cobrem a validação de domínio, o endpoint de saúde e o fluxo HTTP de cliente e conta com PostgreSQL isolado. Conforme os módulos forem implementados, serão adicionados:

- Testes unitários das regras de domínio.
- Testes de integração usando PostgreSQL real em container.
- Verificações de lançamentos balanceados e imutabilidade do ledger.
- Testes de idempotência, concorrência, duplicação e ordenação de mensagens.
- Testes de falha e recuperação da integração simulada.
- Testes de carga que comparem taxa de processamento, atraso de fila e consistência financeira.

## Organização inicial de diretórios

```text
src/
  CoreBanking.Api/
  CoreBanking.Domain/
  CoreBanking.Application/
  CoreBanking.Infrastructure/
  CoreBanking.Worker/
tests/
  CoreBanking.UnitTests/
  CoreBanking.IntegrationTests/
```

A organização poderá evoluir durante a implementação, preservando os limites modulares e a separação entre regras de domínio, casos de uso e infraestrutura.

## Status

**Implementação inicial.** Clientes, contas, ledger, depósitos, transferências e Pix fictícios estão em desenvolvimento. O simulador não é uma integração real ou homologada. As entregas seguintes e seus critérios de aceite estão descritos no [Fluxo de desenvolvimento](./DEVELOPMENT_FLOW.md).

## Referências

- [Política de suporte do .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Versões suportadas do PostgreSQL](https://www.postgresql.org/support/versioning/)
- [Provedores do Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/providers/)
- [Pix — Banco Central do Brasil](https://www.bcb.gov.br/estabilidadefinanceira/pix)
- [Open Finance — Banco Central do Brasil](https://www.bcb.gov.br/estabilidadefinanceira/openfinance)
