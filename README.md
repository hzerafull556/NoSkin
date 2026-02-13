# NoSkins Plugin

Plugin robusto para servidores Unturned que controla e remove skins/cosméticos de jogadores, garantindo uma experiência de jogo uniforme e sem vantagens visuais.

## 📋 Características

### Controle de Cosméticos
- **Bloqueio de Skins**: Impede uso de skins de inventário
- **Bloqueio de Cosméticos**: Remove itens cosméticos (DLC, Workshop, Twitch)
- **Bloqueio de Mythics**: Controla itens míticos/premium
- **Detecção Inteligente**: Sistema de cache para identificação rápida de cosméticos

### Roupa Inicial (Starter Outfit)
- Aplicação automática de roupa padrão no primeiro join
- Configuração completa de cada peça de roupa
- Efeito visual opcional ao aplicar roupa
- Validação de IDs de itens para evitar erros

### Sistema de Permissões
- **noskins.bypass**: Permite jogadores VIP/Staff usarem skins
- Verificação automática de permissões

### Performance e Estabilidade
- Sistema de monitoramento contínuo (coroutine)
- Cache de cosméticos para reduzir verificações
- Thread-safe com locks otimizados
- Sistema de debounce para salvamento de dados
- Proteção contra race conditions

### Recuperação de Estado
- Detecção de respawn do jogador
- Re-aplicação de roupa após morte
- Rastreamento de estados (vivo/morto)
- Sistema de janela temporal para aplicação pós-respawn

## 🚀 Instalação

1. Baixe o plugin e coloque na pasta `Plugins` do RocketMod
2. Inicie o servidor para gerar o arquivo de configuração
3. Configure o `NoSkins.configuration.xml` conforme necessário
4. Reinicie o servidor

## ⚙️ Configuração

```xml
<?xml version="1.0" encoding="utf-8"?>
<NoSkinsConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  
  <!-- Bloqueios gerais -->
  <BlockCosmetics>true</BlockCosmetics>
  <BlockSkins>true</BlockSkins>
  <BlockMythics>true</BlockMythics>
  
  <!-- Remover roupas no primeiro join -->
  <RemoveWearablesOnFirstJoin>true</RemoveWearablesOnFirstJoin>
  
  <!-- Intervalos de verificação (segundos) -->
  <MonitorIntervalSeconds>1</MonitorIntervalSeconds>
  <SaveIntervalSeconds>15</SaveIntervalSeconds>
  
  <!-- Mensagens -->
  <FirstJoinMessage>Seus cosmeticos foram desativados e a roupa inicial foi aplicada.</FirstJoinMessage>
  <SkinBlockedMessage>Skins/Cosmeticos de inventario estao bloqueados neste servidor!</SkinBlockedMessage>
  
  <!-- Flags de detecção de cosméticos -->
  <CosmeticFlags>
    <string>isCosmetic</string>
    <string>isSkin</string>
    <string>isSkinned</string>
    <string>isPro</string>
    <string>isMythic</string>
    <string>isMythical</string>
    <string>isWorkshop</string>
  </CosmeticFlags>
  
  <!-- Palavras-chave no nome do item -->
  <CosmeticNameKeywords>
    <string>cosmetic</string>
    <string>skin</string>
    <string>premium</string>
    <string>dlc</string>
    <string>workshop</string>
    <string>twitch</string>
    <string>mythic</string>
  </CosmeticNameKeywords>
  
  <!-- Configuração da roupa inicial -->
  <StarterOutfit>
    <ShirtId>211</ShirtId>        <!-- Plaid Shirt -->
    <PantsId>212</PantsId>        <!-- Khaki Pants -->
    <HatId>0</HatId>              <!-- 0 = sem item -->
    <BackpackId>0</BackpackId>
    <VestId>0</VestId>
    <MaskId>0</MaskId>
    <GlassesId>0</GlassesId>
    <Quality>100</Quality>        <!-- Qualidade dos itens (0-100) -->
    <PlayEffect>true</PlayEffect> <!-- Efeito visual ao vestir -->
  </StarterOutfit>
  
</NoSkinsConfiguration>
```

### IDs de Roupas Comuns

| Peça | ID | Nome |
|------|-----|------|
| Camisa | 211 | Plaid Shirt |
| Calça | 212 | Khaki Pants |
| Camisa | 3089 | Orange Tee |
| Calça | 3090 | Blue Jeans |

Consulte a [Wiki do Unturned](https://unturned.fandom.com/wiki/Clothing) para mais IDs.

## 🔧 Funcionalidades Técnicas

### Sistema de Detecção
O plugin usa múltiplos métodos para identificar cosméticos:

1. **Flags de Item**: Verifica propriedades como `isCosmetic`, `isMythic`, `isSkin`
2. **Keywords**: Analisa o nome do item procurando palavras-chave
3. **Reflection**: Usa reflection para acessar propriedades privadas do item
4. **Cache**: Armazena resultados para otimizar verificações futuras

### Proteções Implementadas

- **Thread Safety**: Uso de locks para operações concorrentes
- **Null Safety**: Verificações extensivas contra null
- **Disconnection Handling**: Detecta jogadores desconectados antes de processar
- **State Tracking**: Rastreamento de estados de jogador (inicialização, morte, respawn)
- **Debounced I/O**: Salvamento de dados com debounce para evitar I/O excessivo

### Fluxo de Aplicação

```
Player Join
    ↓
Verificar se é primeiro join
    ↓
[SIM] → Remover todas as roupas → Aplicar StarterOutfit → Salvar estado
    ↓
[NÃO] → Verificar permissão bypass
    ↓
[SEM BYPASS] → Monitorar continuamente → Remover cosméticos detectados
```

## 📝 Permissões

| Permissão | Descrição |
|-----------|-----------|
| `noskins.bypass` | Permite o jogador usar skins e cosméticos livremente |

**Exemplo de uso:**
```xml
<!-- Permissions.config.xml -->
<Group>
  <Id>vip</Id>
  <DisplayName>VIP</DisplayName>
  <Permissions>
    <Permission>noskins.bypass</Permission>
  </Permissions>
</Group>
```

## 🐛 Resolução de Problemas

### Skins não estão sendo removidas
- Verifique se `BlockSkins` está `true` na configuração
- Confirme que o jogador não tem a permissão `noskins.bypass`
- Verifique os logs do servidor para erros

### Roupa inicial não aparece
- Confirme que os IDs de roupa são válidos
- Use IDs de roupas vanilla (não workshop)
- Verifique se `RemoveWearablesOnFirstJoin` está `true`

### Performance ruim
- Aumente `MonitorIntervalSeconds` para 2-3 segundos
- Aumente `SaveIntervalSeconds` para 30-60 segundos
- Reduza as flags e keywords de detecção se não necessárias

## 📊 Arquivos Gerados

### first_join_players.txt
Armazena os SteamIDs dos jogadores que já tiveram a roupa inicial aplicada.
```
76561198012345678
76561198087654321
```

Este arquivo garante que a roupa só é aplicada uma vez por jogador.

## 🔄 Compatibilidade

- **RocketMod**: 4.9.3.0+
- **Unturned**: Versões recentes (testado em 3.23.x)
- **.NET Framework**: 4.6.1+

## 🤝 Contribuindo

Contribuições são bem-vindas! Por favor:

1. Faça um fork do projeto
2. Crie uma branch para sua feature (`git checkout -b feature/MinhaFeature`)
3. Commit suas mudanças (`git commit -m 'Adiciona MinhaFeature'`)
4. Push para a branch (`git push origin feature/MinhaFeature`)
5. Abra um Pull Request

## 📄 Licença

Este projeto está sob a licença MIT. Veja o arquivo `LICENSE` para mais detalhes.

## 💡 Suporte

Para reportar bugs ou solicitar features:
- Abra uma [Issue](https://github.com/seu-usuario/noskins/issues)
- Entre em contato via Discord: [seu-discord]

## ✨ Créditos

Desenvolvido por [Seu Nome/Leites]

---

**Nota**: Este plugin foi desenvolvido para promover equidade visual em servidores PvP e melhorar a performance ao reduzir assets carregados.
