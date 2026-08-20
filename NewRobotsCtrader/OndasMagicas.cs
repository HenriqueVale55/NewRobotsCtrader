using System;
using System.Linq;
using System.Reflection.Emit;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class OndasMagicas : Robot
    {
        private const string RobotDisplayName = "Ondas Mágicas";

        // ---------------------------------------------------------
        // Parâmetros - Indicador
        // ---------------------------------------------------------
        [Parameter("Tipo da Média", DefaultValue = MovingAverageType.Simple, Group = "Indicador")]
        public MovingAverageType MaType { get; set; }

        [Parameter("Período da Média", DefaultValue = 20, Group = "Indicador")]
        public int MaPeriod { get; set; }

        [Parameter("Fonte de Preço", Group = "Indicador")]
        public DataSeries SourceSeries { get; set; }

        [Parameter("Período do Desvio Padrão", DefaultValue = 20, Group = "Indicador")]
        public int StdDevPeriod { get; set; }

        [Parameter("Multiplicador do Desvio Padrão", DefaultValue = 1.0, Group = "Indicador")]
        public double SdMultiplier { get; set; }

        // ---------------------------------------------------------
        // Parâmetros - Trading
        // ---------------------------------------------------------
        [Parameter("Distância Adicional (Pips)", DefaultValue = 10.0, Group = "Trading")]
        public double ExtraDistancePips { get; set; }

        [Parameter("Volume Inicial", DefaultValue = 1000, Group = "Trading")]
        public double InitialVolume { get; set; }

        [Parameter("Multiplicador de Lote (1 = Fixo)", DefaultValue = 1.0, Group = "Trading")]
        public double LotMultiplier { get; set; }

        [Parameter("Máximo de Posições (Bandas)", DefaultValue = 5, Group = "Trading")]
        public int MaxPositions { get; set; }

        [Parameter("Take Profit Global (Pips)", DefaultValue = 10, Group = "Trading")]
        public double TakeProfitPips { get; set; }

        [Parameter("Label / Magic Number", DefaultValue = "OndasMagicas", Group = "Trading")]
        public string BotLabel { get; set; }

        // ---------------------------------------------------------
        // Parâmetros - Controle Diário
        // ---------------------------------------------------------
        [Parameter("Limite Ganho Diário ($)", DefaultValue = 100, Group = "Controle Diário")]
        public double DailyProfitLimit { get; set; }

        [Parameter("Limite Perda Diário ($)", DefaultValue = 100, Group = "Controle Diário")]
        public double DailyLossLimit { get; set; }

        [Parameter("Fechar Operações ao Atingir Limite?", DefaultValue = true, Group = "Controle Diário")]
        public bool CloseOnLimitHit { get; set; }

        // ---------------------------------------------------------
        // Parâmetros - Filtro de Horário
        // ---------------------------------------------------------
        [Parameter("Hora Início (0-23)", DefaultValue = 9, Group = "Filtro de Horário")]
        public int StartHour { get; set; }

        [Parameter("Minuto Início (0-59)", DefaultValue = 0, Group = "Filtro de Horário")]
        public int StartMinute { get; set; }

        [Parameter("Hora Fim (0-23)", DefaultValue = 17, Group = "Filtro de Horário")]
        public int EndHour { get; set; }

        [Parameter("Minuto Fim (0-59)", DefaultValue = 30, Group = "Filtro de Horário")]
        public int EndMinute { get; set; }

        // ---------------------------------------------------------
        // Parâmetros - Painel
        // ---------------------------------------------------------
        [Parameter("Posição Horizontal", DefaultValue = HorizontalAlignment.Right, Group = "Painel")]
        public HorizontalAlignment PanelHorizontal { get; set; }

        [Parameter("Posição Vertical", DefaultValue = VerticalAlignment.Top, Group = "Painel")]
        public VerticalAlignment PanelVertical { get; set; }

        [Parameter("Tamanho da Fonte", DefaultValue = 11, Group = "Painel")]
        public int PanelFontSize { get; set; }

        // ---------------------------------------------------------
        // Variáveis de Estado
        // ---------------------------------------------------------
        private MovingAverage _ma;
        private StandardDeviation _stdDev;

        private int _currentDay;
        private double _dailyPnL;
        private bool _dailyLimitHit;
        private bool _isPausedZone;

        // Estado do painel / botão liga-desliga
        private bool _botEnabled = true;

        // Controles do painel
        private Border _panelBorder;
        private TextBlock _txtStatus;
        private TextBlock _txtSpread;
        private TextBlock _txtDailyResult;
        private TextBlock _txtFloating;
        private Button _btnToggle;

        protected override void OnStart()
        {
            _ma = Indicators.MovingAverage(SourceSeries, MaPeriod, MaType);
            _stdDev = Indicators.StandardDeviation(SourceSeries, StdDevPeriod, MaType);

            _currentDay = Server.Time.Day;

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;

            CreatePanel();
        }

        protected override void OnStop()
        {
            try
            {
                if (_panelBorder != null)
                    Chart.RemoveControl(_panelBorder);
            }
            catch (Exception ex)
            {
                Print("Erro ao remover painel: " + ex.Message);
            }
        }

        protected override void OnTick()
        {
            CheckNewDay();
            CheckDailyLimits();

            // O painel é atualizado independentemente do estado de trava diária,
            // para que o usuário sempre veja o status atual do robô.
            UpdatePanel();

            if (_dailyLimitHit) return;

            ManagePauseState();

            var openPositions = Positions.Where(p => p.Label == BotLabel).ToArray();
            var pendingOrders = PendingOrders.Where(o => o.Label == BotLabel).ToArray();

            bool isTimeValid = IsTimeValid();

            if (openPositions.Length == 0)
            {
                if (_isPausedZone) return;

                double ma = _ma.Result.LastValue;
                double sd = _stdDev.Result.LastValue;

                // Cálculo da distância ajustada (Desvio Padrão + Pips Adicionais)
                double stepDistance = (sd * SdMultiplier) + (ExtraDistancePips * Symbol.PipSize);

                double upperBand1 = ma + stepDistance;
                double lowerBand1 = ma - stepDistance;

                if (Symbol.Bid > ma && Symbol.Bid < upperBand1 && isTimeValid)
                {
                    CancelOppositeOrders(TradeType.Buy);
                    ManageDynamicEntry(TradeType.Sell, upperBand1, pendingOrders, 0);
                }
                else if (Symbol.Ask < ma && Symbol.Ask > lowerBand1 && isTimeValid)
                {
                    CancelOppositeOrders(TradeType.Sell);
                    ManageDynamicEntry(TradeType.Buy, lowerBand1, pendingOrders, 0);
                }
                else
                {
                    CancelAllPendingOrders();
                }
            }
            else
            {
                ManageGrid(openPositions, pendingOrders);
            }
        }

        // ---------------------------------------------------------
        // Lógica de Entradas e Grid
        // ---------------------------------------------------------
        private void ManageDynamicEntry(TradeType direction, double targetPrice, PendingOrder[] pendingOrders, int currentPositionCount)
        {
            // Interruptor do painel: bloqueia apenas NOVAS entradas (inclui novos níveis de grid).
            // Gerenciamento de posições já abertas (TP, pausa, limites diários) continua funcionando normalmente.
            if (!_botEnabled) return;

            double normalizedPrice = Math.Round(targetPrice, Symbol.Digits);

            // Proteção da API: Ajusta limite dependendo do tipo da ordem para evitar rejeição
            if (direction == TradeType.Buy && normalizedPrice >= Symbol.Ask)
                normalizedPrice = Symbol.Ask - Symbol.TickSize;
            else if (direction == TradeType.Sell && normalizedPrice <= Symbol.Bid)
                normalizedPrice = Symbol.Bid + Symbol.TickSize;

            if (pendingOrders.Length == 0)
            {
                double volume = GetVolumeForNextOrder(currentPositionCount);
                PlaceLimitOrder(direction, SymbolName, volume, normalizedPrice, BotLabel);
            }
            else
            {
                var order = pendingOrders[0];

                // Só modifica a ordem se a variação justificar (evita sobrecarga da API)
                if (Math.Abs(order.TargetPrice - normalizedPrice) > Symbol.TickSize)
                {
                    ModifyPendingOrder(order, normalizedPrice, (double?)null, (double?)null);
                }
            }
        }

        private void ManageGrid(Position[] positions, PendingOrder[] pendingOrders)
        {
            TradeType gridDirection = positions[0].TradeType;
            int currentLevel = positions.Length;

            if (currentLevel >= MaxPositions)
            {
                CancelAllPendingOrders();
                return;
            }

            int nextMultiplier = currentLevel + 1;
            double ma = _ma.Result.LastValue;
            double sd = _stdDev.Result.LastValue;

            // Cálculo da distância ajustada para os próximos níveis do Grid
            double stepDistance = (sd * SdMultiplier) + (ExtraDistancePips * Symbol.PipSize);

            double targetPrice = gridDirection == TradeType.Sell
                ? ma + (stepDistance * nextMultiplier)
                : ma - (stepDistance * nextMultiplier);

            ManageDynamicEntry(gridDirection, targetPrice, pendingOrders, currentLevel);
        }

        private double GetVolumeForNextOrder(int currentPositions)
        {
            double rawVolume = InitialVolume * Math.Pow(LotMultiplier, currentPositions);
            return Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.ToNearest);
        }

        // ---------------------------------------------------------
        // Eventos e Take Profit Global
        // ---------------------------------------------------------
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (args.Position.Label != BotLabel) return;
            RecalculateTakeProfit();
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != BotLabel) return;

            _dailyPnL += args.Position.NetProfit;
            CheckDailyLimits();

            var remainingPositions = Positions.Where(p => p.Label == BotLabel).ToArray();

            if (remainingPositions.Length > 0)
            {
                // Se sobrarem posições após um fechamento (ex.: stop out, fechamento manual),
                // o preço médio mudou e o TP global precisa ser recalculado para refletir isso.
                RecalculateTakeProfit();
            }
            else
            {
                CancelAllPendingOrders();

                // Verifica se encerrou fora da zona inicial aceitável (baseada na nova distância)
                double ma = _ma.Result.LastValue;
                double sd = _stdDev.Result.LastValue;
                double stepDistance = (sd * SdMultiplier) + (ExtraDistancePips * Symbol.PipSize);

                double upperBand1 = ma + stepDistance;
                double lowerBand1 = ma - stepDistance;

                if (Symbol.Bid > upperBand1 || Symbol.Ask < lowerBand1)
                {
                    _isPausedZone = true;
                }
            }
        }

        private void RecalculateTakeProfit()
        {
            var allPositions = Positions.Where(p => p.Label == BotLabel).ToArray();
            if (allPositions.Length == 0) return;

            TradeType type = allPositions[0].TradeType;

            // Filtra explicitamente pela direção do grid ativo, evitando que uma posição
            // remanescente de direção oposta distorça o cálculo do preço médio.
            var positions = allPositions.Where(p => p.TradeType == type).ToArray();

            double totalVolume = 0;
            double totalCost = 0;

            foreach (var pos in positions)
            {
                totalVolume += pos.VolumeInUnits;
                totalCost += pos.EntryPrice * pos.VolumeInUnits;
            }

            if (totalVolume <= 0) return;

            double averagePrice = totalCost / totalVolume;
            double tpOffset = TakeProfitPips * Symbol.PipSize;

            double tpPrice = type == TradeType.Buy
                ? averagePrice + tpOffset
                : averagePrice - tpOffset;

            tpPrice = Math.Round(tpPrice, Symbol.Digits);

            foreach (var pos in positions)
            {
                if (!pos.TakeProfit.HasValue || Math.Abs(pos.TakeProfit.Value - tpPrice) > Symbol.TickSize)
                {
                    ModifyPosition(pos, null, tpPrice);
                }
            }
        }

        // ---------------------------------------------------------
        // Filtros e Travas
        // ---------------------------------------------------------
        private void ManagePauseState()
        {
            if (!_isPausedZone) return;

            double ma = _ma.Result.LastValue;
            double sd = _stdDev.Result.LastValue;
            double stepDistance = (sd * SdMultiplier) + (ExtraDistancePips * Symbol.PipSize);

            double upperBand1 = ma + stepDistance;
            double lowerBand1 = ma - stepDistance;

            // Se o preço retornar para entre a Banda -1 e a Banda +1, libera o robô
            if (Symbol.Bid <= upperBand1 && Symbol.Ask >= lowerBand1)
            {
                _isPausedZone = false;
            }
        }

        private void CheckNewDay()
        {
            if (Server.Time.Day != _currentDay)
            {
                _currentDay = Server.Time.Day;
                _dailyPnL = 0;
                _dailyLimitHit = false;
            }
        }

        private void CheckDailyLimits()
        {
            if (_dailyLimitHit) return;

            double openPnL = Positions.Where(p => p.Label == BotLabel).Sum(p => p.NetProfit);
            double totalDailyPnL = _dailyPnL + openPnL;

            if (totalDailyPnL <= -DailyLossLimit || totalDailyPnL >= DailyProfitLimit)
            {
                _dailyLimitHit = true;
                CancelAllPendingOrders();

                if (CloseOnLimitHit)
                {
                    foreach (var position in Positions.Where(p => p.Label == BotLabel))
                    {
                        ClosePosition(position);
                    }
                }
            }
        }

        private bool IsTimeValid()
        {
            TimeSpan now = Server.Time.TimeOfDay;
            TimeSpan start = new TimeSpan(StartHour, StartMinute, 0);
            TimeSpan end = new TimeSpan(EndHour, EndMinute, 0);

            if (start < end)
                return now >= start && now <= end;
            else
                return now >= start || now <= end;
        }

        private void CancelAllPendingOrders()
        {
            foreach (var order in PendingOrders.Where(o => o.Label == BotLabel))
            {
                CancelPendingOrder(order);
            }
        }

        private void CancelOppositeOrders(TradeType typeToCancel)
        {
            foreach (var order in PendingOrders.Where(o => o.Label == BotLabel && o.TradeType == typeToCancel))
            {
                CancelPendingOrder(order);
            }
        }

        // ---------------------------------------------------------
        // Painel Visual de Monitoramento e Controle
        // ---------------------------------------------------------
        private void CreatePanel()
        {
            try
            {
                var mainStack = new StackPanel
                {
                    BackgroundColor = Color.FromArgb(230, 25, 25, 25),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(15),
                    Width = 220
                };

                _panelBorder = new Border
                {
                    BorderColor = Color.FromArgb(255, 75, 75, 75),
                    BorderThickness = new Thickness(1),
                    Child = mainStack,
                    HorizontalAlignment = PanelHorizontal,
                    VerticalAlignment = PanelVertical,
                    Margin = new Thickness(15)
                };

                mainStack.AddChild(CreateText($"ROBÔ: {RobotDisplayName}", Color.Gold, true));
                mainStack.AddChild(CreateDivider());

                _txtStatus = CreateText("Status: ● ATIVO", Color.Lime, true);
                mainStack.AddChild(_txtStatus);

                mainStack.AddChild(CreateText($"Ativo: {SymbolName}", Color.WhiteSmoke));

                _txtSpread = CreateText("Spread: -- pips", Color.WhiteSmoke);
                mainStack.AddChild(_txtSpread);

                mainStack.AddChild(CreateDivider());
                mainStack.AddChild(CreateText($"Meta lucro: +${DailyProfitLimit:F2}", Color.LightSkyBlue));
                mainStack.AddChild(CreateText($"Meta perda: -${DailyLossLimit:F2}", Color.LightPink));

                mainStack.AddChild(CreateDivider());

                _txtDailyResult = CreateText("Resultado dia: --", Color.WhiteSmoke, true);
                mainStack.AddChild(_txtDailyResult);

                _txtFloating = CreateText("Flutuante: --", Color.WhiteSmoke);
                mainStack.AddChild(_txtFloating);

                _btnToggle = new Button
                {
                    Text = "DESLIGAR ROBÔ",
                    BackgroundColor = Color.Crimson,
                    ForegroundColor = Color.White,
                    Margin = new Thickness(5, 10, 5, 5),
                    Height = 25,
                    FontWeight = FontWeight.Bold
                };

                _btnToggle.Click += OnToggleButtonClick;

                mainStack.AddChild(CreateDivider());
                mainStack.AddChild(_btnToggle);

                Chart.AddControl(_panelBorder);
            }
            catch (Exception ex)
            {
                Print("Erro ao construir interface gráfica: " + ex.Message);
            }
        }

        private TextBlock CreateText(string text, Color color, bool bold = false)
        {
            return new TextBlock
            {
                Text = text,
                ForegroundColor = color,
                FontWeight = bold ? FontWeight.ExtraBold : FontWeight.Normal,
                FontSize = PanelFontSize,
                Margin = new Thickness(5, 3, 5, 3)
            };
        }

        private Border CreateDivider()
        {
            return new Border
            {
                BackgroundColor = Color.FromArgb(255, 60, 60, 60),
                Height = 1,
                Margin = new Thickness(5, 2, 5, 2)
            };
        }

        private void OnToggleButtonClick(ButtonClickEventArgs args)
        {
            try
            {
                _botEnabled = !_botEnabled;

                if (_botEnabled)
                {
                    _txtStatus.Text = "Status: ● ATIVO";
                    _txtStatus.ForegroundColor = Color.Lime;
                    _btnToggle.Text = "DESLIGAR ROBÔ";
                    _btnToggle.BackgroundColor = Color.Crimson;
                }
                else
                {
                    _txtStatus.Text = "Status: ○ DESATIVADO";
                    _txtStatus.ForegroundColor = Color.Tomato;
                    _btnToggle.Text = "LIGAR ROBÔ";
                    _btnToggle.BackgroundColor = Color.SeaGreen;
                    CancelAllPendingOrders();
                }

                // Se o limite diário estiver atingido, ele continua prevalecendo visualmente.
                if (_dailyLimitHit)
                    RefreshPanelStaticTexts();
            }
            catch (Exception ex)
            {
                Print("Erro no botão do painel: " + ex.Message);
            }
        }

        private void UpdatePanel()
        {
            if (_panelBorder == null) return;

            try
            {
                double currentSpreadPips = Symbol.PipSize > 0
                    ? (Symbol.Ask - Symbol.Bid) / Symbol.PipSize
                    : 0;

                _txtSpread.Text = $"Spread: {currentSpreadPips:F1} pips";

                if (currentSpreadPips > 0)
                    _txtSpread.ForegroundColor = Color.WhiteSmoke;

                double floating = 0;
                var openPositions = Positions.Where(p => p.Label == BotLabel);

                if (openPositions.Any())
                    floating = openPositions.Sum(p => p.NetProfit);

                _txtFloating.Text =
                    $"Flutuante: {(floating >= 0 ? "+" : "-")}${Math.Abs(floating):F2}";
                _txtFloating.ForegroundColor =
                    floating >= 0 ? Color.Lime : Color.Tomato;

                if (_dailyLimitHit)
                {
                    if (_dailyPnL >= DailyProfitLimit)
                    {
                        _txtDailyResult.Text = "META DE LUCRO ATINGIDA";
                        _txtDailyResult.ForegroundColor = Color.Lime;
                    }
                    else if (_dailyPnL <= -DailyLossLimit)
                    {
                        _txtDailyResult.Text = "LIMITE DE PERDA ATINGIDO";
                        _txtDailyResult.ForegroundColor = Color.Tomato;
                    }
                    else
                    {
                        _txtDailyResult.Text =
                            $"Resultado dia: {(_dailyPnL >= 0 ? "+" : "-")}${Math.Abs(_dailyPnL):F2}";
                        _txtDailyResult.ForegroundColor =
                            _dailyPnL >= 0 ? Color.Lime : Color.Tomato;
                    }
                }
                else
                {
                    _txtDailyResult.Text =
                        $"Resultado dia: {(_dailyPnL >= 0 ? "+" : "-")}${Math.Abs(_dailyPnL):F2}";
                    _txtDailyResult.ForegroundColor =
                        _dailyPnL >= 0 ? Color.Lime : Color.Tomato;
                }

                RefreshPanelStaticTexts();
            }
            catch (Exception ex)
            {
                Print("Falha ao atualizar painel visual: " + ex.Message);
            }
        }

        private void RefreshPanelStaticTexts()
        {
            if (_txtStatus == null) return;

            if (_dailyLimitHit)
            {
                if (_dailyPnL >= DailyProfitLimit)
                {
                    _txtStatus.Text = "Status: ● META DE LUCRO ATINGIDA";
                    _txtStatus.ForegroundColor = Color.Gold;
                }
                else
                {
                    _txtStatus.Text = "Status: ● LIMITE DE PERDA ATINGIDO";
                    _txtStatus.ForegroundColor = Color.Tomato;
                }
            }
            else if (_botEnabled)
            {
                _txtStatus.Text = "Status: ● ATIVO";
                _txtStatus.ForegroundColor = Color.Lime;
            }
            else
            {
                _txtStatus.Text = "Status: ○ DESATIVADO";
                _txtStatus.ForegroundColor = Color.Tomato;
            }

            if (_btnToggle != null)
            {
                _btnToggle.Text = _botEnabled
                    ? "DESLIGAR ROBÔ"
                    : "LIGAR ROBÔ";

                _btnToggle.BackgroundColor = _botEnabled
                    ? Color.Crimson
                    : Color.SeaGreen;
            }
        }
    }
}