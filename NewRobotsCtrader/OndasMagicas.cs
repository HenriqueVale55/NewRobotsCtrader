using System;
using System.Linq;
using System.Text;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class SuperTrend : Robot
    {
        [Parameter("Volume (Lotes)", Group = "Operacional", DefaultValue = 0.1, MinValue = 0.01)]
        public double VolumeLots { get; set; }

        [Parameter("Início das Operações (HH:mm)", Group = "Horário de Negociação", DefaultValue = "00:00")]
        public string StartTimeStr { get; set; }

        [Parameter("Fim das Operações (HH:mm)", Group = "Horário de Negociação", DefaultValue = "23:59")]
        public string EndTimeStr { get; set; }

        [Parameter("Max Spread Permitido (Pips)", Group = "Proteção de Spread", DefaultValue = 3.0, MinValue = 0.1)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Habilitar Filtro SMA", Group = "Filtro de Tendência", DefaultValue = true)]
        public bool EnableSmaFilter { get; set; }

        [Parameter("SMA Period", Group = "Filtro de Tendência", DefaultValue = 200)]
        public int SmaPeriod { get; set; }

        [Parameter("SuperTrend Period", Group = "SuperTrend", DefaultValue = 10)]
        public int SuperTrendPeriod { get; set; }

        [Parameter("SuperTrend Multiplier", Group = "SuperTrend", DefaultValue = 3.0)]
        public double SuperTrendMultiplier { get; set; }

        [Parameter("Habilitar Saída Parcial", Group = "Saída Parcial", DefaultValue = false)]
        public bool EnablePartialExit { get; set; }

        [Parameter("Volume Parcial (Lotes)", Group = "Saída Parcial", DefaultValue = 0.05, MinValue = 0.01)]
        public double PartialVolumeLots { get; set; }

        [Parameter("ATR Period (Para Parcial)", Group = "Saída Parcial", DefaultValue = 14)]
        public int AtrPeriod { get; set; }

        [Parameter("Alvo Parcial (ATR Multiplier)", Group = "Saída Parcial", DefaultValue = 1.0)]
        public double PartialAtrMultiplier { get; set; }

        [Parameter("Distância do Grid (Pips)", Group = "Grid (A Favor da Tendência)", DefaultValue = 20.0, MinValue = 1.0)]
        public double GridDistancePips { get; set; }

        [Parameter("Max Ordens Grid", Group = "Grid (A Favor da Tendência)", DefaultValue = 5)]
        public int MaxGridOrders { get; set; }

        [Parameter("Volume Grid (Lotes)", Group = "Grid (A Favor da Tendência)", DefaultValue = 0.05, MinValue = 0.01)]
        public double GridVolumeLots { get; set; }

        [Parameter("Limite de Ganho Diário ($)", Group = "Gestão de Risco", DefaultValue = 100)]
        public double DailyProfitLimit { get; set; }

        [Parameter("Limite de Perda Diária ($)", Group = "Gestão de Risco", DefaultValue = 50)]
        public double DailyLossLimit { get; set; }

        [Parameter("Habilitar Proteção de Lucro", Group = "Gestão de Risco", DefaultValue = true)]
        public bool EnableProfitProtection { get; set; }

        [Parameter("Gatilho da Proteção (% da Meta)", Group = "Gestão de Risco", DefaultValue = 80.0, MinValue = 1.0)]
        public double ProtectionTriggerPercent { get; set; }

        [Parameter("Lucro Protegido (% do Ganho)", Group = "Gestão de Risco", DefaultValue = 50.0, MinValue = 1.0)]
        public double ProtectionLockPercent { get; set; }

        // Variável constante para a etiqueta das ordens do Bot
        private const string BotLabel = "ST_Bot";

        private Supertrend _superTrend;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _sma;
        private bool _stoppedForToday;
        private DateTime _lastTradeDay;

        private bool _firstFlipOccurred;
        private double _partialTargetPrice;
        private bool _partialExecuted;

        private bool _spreadTooHigh; // Variável do filtro de Spread
        private TimeSpan _startTime; // Variável do filtro de Horário
        private TimeSpan _endTime;   // Variável do filtro de Horário

        // Variáveis da Proteção de Capital
        private double _highestDailyProfit;
        private bool _protectionActivated;
        private bool _stoppedByProtection;

        // --- VARIÁVEIS DE CONTROLE DO PAINEL UI ---
        private Border _mainPanelBorder;
        private TextBlock _statusText;
        private TextBlock _spreadText;
        private TextBlock _dailyResultText;
        private TextBlock _floatingText;
        private Button _toggleButton;
        private bool _botEnabled = true;

        protected override void OnStart()
        {
            _superTrend = Indicators.Supertrend(SuperTrendPeriod, SuperTrendMultiplier);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            _sma = Indicators.SimpleMovingAverage(Bars.ClosePrices, SmaPeriod);
            _lastTradeDay = Server.Time.Date;
            _firstFlipOccurred = false;
            _spreadTooHigh = false;
            _highestDailyProfit = 0;
            _protectionActivated = false;
            _stoppedByProtection = false;

            if (!TimeSpan.TryParse(StartTimeStr, out _startTime)) _startTime = TimeSpan.Zero;
            if (!TimeSpan.TryParse(EndTimeStr, out _endTime)) _endTime = new TimeSpan(23, 59, 59);

            BuildUI();
        }

        protected override void OnStop()
        {
            if (_mainPanelBorder != null)
            {
                Chart.RemoveControl(_mainPanelBorder);
            }
        }

        protected override void OnTick()
        {
            if (Server.Time.Date != _lastTradeDay)
            {
                _stoppedForToday = false;
                _stoppedByProtection = false;
                _highestDailyProfit = 0;
                _protectionActivated = false;
                _lastTradeDay = Server.Time.Date;
            }

            UpdateUI();

            if (_stoppedForToday) return;

            // --- PROTEÇÃO DE SPREAD ALTO ---
            double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
            _spreadTooHigh = currentSpreadPips > MaxSpreadPips;

            CheckDailyLimits();
            CheckPartialExit();
        }

        protected override void OnBar()
        {
            if (_stoppedForToday) return;

            bool upTrendCurrent = !double.IsNaN(_superTrend.UpTrend.Last(1));
            bool downTrendCurrent = !double.IsNaN(_superTrend.DownTrend.Last(1));

            bool upTrendPrev = !double.IsNaN(_superTrend.UpTrend.Last(2));
            bool downTrendPrev = !double.IsNaN(_superTrend.DownTrend.Last(2));

            bool flippedToBuy = upTrendCurrent && downTrendPrev;
            bool flippedToSell = downTrendCurrent && upTrendPrev;

            double currentClose = Bars.ClosePrices.Last(1);
            double currentSma = _sma.Result.Last(1);

            bool smaAllowBuy = !EnableSmaFilter || (currentClose > currentSma);
            bool smaAllowSell = !EnableSmaFilter || (currentClose < currentSma);

            TimeSpan currentTime = Server.Time.TimeOfDay;
            bool isTradingTime = _startTime <= _endTime
                ? (currentTime >= _startTime && currentTime <= _endTime)
                : (currentTime >= _startTime || currentTime <= _endTime);

            bool isAllowedBuy = isTradingTime && smaAllowBuy && !_spreadTooHigh && _botEnabled;
            bool isAllowedSell = isTradingTime && smaAllowSell && !_spreadTooHigh && _botEnabled;

            if (flippedToBuy)
            {
                _firstFlipOccurred = true;

                CloseAndCancelAll(); // Saída total pela virada do indicador

                if (isAllowedBuy)
                {
                    var volumeInUnits = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(VolumeLots));
                    ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, BotLabel);

                    SetPartialTarget(TradeType.Buy);
                    ManageDynamicGrid(TradeType.Buy, isAllowedBuy);
                }
            }
            else if (flippedToSell)
            {
                _firstFlipOccurred = true;

                CloseAndCancelAll(); // Saída total pela virada do indicador

                if (isAllowedSell)
                {
                    var volumeInUnits = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(VolumeLots));
                    ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, BotLabel);

                    SetPartialTarget(TradeType.Sell);
                    ManageDynamicGrid(TradeType.Sell, isAllowedSell);
                }
            }
            else if (_firstFlipOccurred)
            {
                if (upTrendCurrent)
                {
                    ManageDynamicGrid(TradeType.Buy, isAllowedBuy);
                }
                else if (downTrendCurrent)
                {
                    ManageDynamicGrid(TradeType.Sell, isAllowedSell);
                }
            }
        }

        private void SetPartialTarget(TradeType tradeType)
        {
            _partialExecuted = false;
            if (!EnablePartialExit) return;

            double atrValue = _atr.Result.Last(1);
            double currentClose = Bars.ClosePrices.Last(1);

            if (tradeType == TradeType.Buy)
                _partialTargetPrice = currentClose + (atrValue * PartialAtrMultiplier);
            else
                _partialTargetPrice = currentClose - (atrValue * PartialAtrMultiplier);
        }

        private void CheckPartialExit()
        {
            if (!EnablePartialExit || _partialExecuted) return;

            // Busca a posição principal (a mais antiga) para aplicar a saída parcial
            var mainPosition = Positions.FindAll(BotLabel, SymbolName).OrderBy(p => p.EntryTime).FirstOrDefault();

            if (mainPosition != null)
            {
                double currentLivePrice = mainPosition.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                bool targetReached = false;

                if (mainPosition.TradeType == TradeType.Buy && currentLivePrice >= _partialTargetPrice)
                    targetReached = true;
                else if (mainPosition.TradeType == TradeType.Sell && currentLivePrice <= _partialTargetPrice)
                    targetReached = true;

                if (targetReached)
                {
                    double partialVolume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(PartialVolumeLots));

                    if (partialVolume < mainPosition.VolumeInUnits)
                        ClosePosition(mainPosition, partialVolume);
                    else
                        ClosePosition(mainPosition);

                    _partialExecuted = true;
                }
            }
        }

        // --- GRID A FAVOR DA TENDÊNCIA (PYRAMIDING) ---
        private void ManageDynamicGrid(TradeType tradeType, bool isAllowed = true)
        {
            if (MaxGridOrders <= 0) return;

            var pendingOrders = PendingOrders.Where(x => x.Label == BotLabel && x.SymbolName == SymbolName).ToArray();
            var activePositions = Positions.FindAll(BotLabel, SymbolName);

            // Validação de segurança: se a principal não abriu ou já fechou, encerra as ordens
            if (activePositions.Length == 0 || !isAllowed || !_botEnabled)
            {
                foreach (var order in pendingOrders) CancelPendingOrder(order);
                return;
            }

            int filledGridCount = activePositions.Length - 1;
            if (filledGridCount >= MaxGridOrders)
            {
                // Limite máximo atingido, nenhuma nova ordem será posicionada
                foreach (var order in pendingOrders) CancelPendingOrder(order);
                return;
            }

            // Descobre o preço da posição MAIS AVANÇADA já aberta
            double referencePrice = tradeType == TradeType.Buy
                ? activePositions.Max(p => p.EntryPrice)
                : activePositions.Min(p => p.EntryPrice);

            // Calcula onde deve ficar a próxima ordem baseada no distanciamento em Pips
            double distance = GridDistancePips * Symbol.PipSize;
            double nextLevelPrice = tradeType == TradeType.Buy
                ? referencePrice + distance
                : referencePrice - distance;

            nextLevelPrice = Math.Round(nextLevelPrice, Symbol.Digits);
            double gridVolumeInUnits = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(GridVolumeLots));

            // Proteção API: Se por acaso o preço der um salto e já ultrapassar o nível alvo da ordem 
            // entra a mercado instantaneamente para evitar rejeição da Stop Order.
            bool shouldExecuteMarket = false;
            if (tradeType == TradeType.Buy && Symbol.Ask >= nextLevelPrice)
                shouldExecuteMarket = true;
            else if (tradeType == TradeType.Sell && Symbol.Bid <= nextLevelPrice)
                shouldExecuteMarket = true;

            if (shouldExecuteMarket)
            {
                foreach (var order in pendingOrders) CancelPendingOrder(order);
                ExecuteMarketOrder(tradeType, SymbolName, gridVolumeInUnits, BotLabel);
            }
            else
            {
                var pendingOrder = pendingOrders.FirstOrDefault();
                if (pendingOrder == null)
                {
                    // Ordem no formato STOP (esperando o preço atingir e romper o nível de preço a favor)
                    PlaceStopOrder(tradeType, SymbolName, gridVolumeInUnits, nextLevelPrice, BotLabel);
                }
                else if (Math.Abs(pendingOrder.TargetPrice - nextLevelPrice) > Symbol.TickSize)
                {
                    // CORREÇÃO CS0121 APLICADA AQUI: 
                    // Passamos as propriedades de proteção da própria ordem (que já estão nulas)
                    // para resolver a ambiguidade de tipos da API cTrader mais recente.
                    ModifyPendingOrder(pendingOrder, nextLevelPrice, pendingOrder.StopLoss, pendingOrder.TakeProfit);
                }
            }
        }

        private void GetDailyAndFloatingProfit(out double closedProfit, out double openProfit)
        {
            closedProfit = 0;
            foreach (var trade in History.Where(x => x.ClosingTime.Date == Server.Time.Date && x.SymbolName == SymbolName && x.Label == BotLabel))
            {
                closedProfit += trade.NetProfit;
            }

            openProfit = 0;
            foreach (var position in Positions.FindAll(BotLabel, SymbolName))
            {
                openProfit += position.NetProfit;
            }
        }

        private void CheckDailyLimits()
        {
            GetDailyAndFloatingProfit(out double closedProfit, out double openProfit);
            double todayProfit = closedProfit + openProfit;

            if (todayProfit > _highestDailyProfit)
            {
                _highestDailyProfit = todayProfit;
            }

            if (DailyProfitLimit > 0 && EnableProfitProtection && !_protectionActivated)
            {
                if (_highestDailyProfit >= DailyProfitLimit * (ProtectionTriggerPercent / 100.0))
                {
                    _protectionActivated = true;
                }
            }

            if (DailyProfitLimit > 0 && todayProfit >= DailyProfitLimit)
            {
                CloseAndCancelAll();
                _stoppedForToday = true;
                _firstFlipOccurred = false;
            }
            else if (DailyLossLimit > 0 && todayProfit <= -DailyLossLimit)
            {
                CloseAndCancelAll();
                _stoppedForToday = true;
                _firstFlipOccurred = false;
            }
            else if (_protectionActivated && todayProfit <= _highestDailyProfit * (ProtectionLockPercent / 100.0))
            {
                CloseAndCancelAll();
                _stoppedForToday = true;
                _stoppedByProtection = true;
                _firstFlipOccurred = false;
            }
        }

        private void CloseAndCancelAll()
        {
            foreach (var position in Positions.FindAll(BotLabel, SymbolName))
            {
                ClosePosition(position);
            }

            foreach (var order in PendingOrders.Where(x => x.Label == BotLabel && x.SymbolName == SymbolName).ToArray())
            {
                CancelPendingOrder(order);
            }
        }

        protected override void OnPositionClosed(Position position)
        {
            if (position.Label != BotLabel)
                return;

            SaveTradeToCsv(position);
        }

        private void SaveTradeToCsv(Position position)
        {
            try
            {
                string folderPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cTrader_Logs");
                System.IO.Directory.CreateDirectory(folderPath);

                string fileName = $"Operacoes_{Account.Number}_{Server.Time:yyyy-MM-dd}.csv";
                string filePath = System.IO.Path.Combine(folderPath, fileName);
                bool fileExists = System.IO.File.Exists(filePath);

                using (var writer = new System.IO.StreamWriter(filePath, true, Encoding.UTF8))
                {
                    if (!fileExists)
                    {
                        writer.WriteLine("ID;Ativo;Tipo;Volume;DataEntrada;DataSaida;PrecoEntrada;LucroLiquidoUSD;Resultado");
                    }

                    string resultado = position.NetProfit >= 0 ? "WIN" : "LOSS";
                    string line = $"{position.Id};{position.SymbolName};{position.TradeType};{position.Quantity};{position.EntryTime:yyyy-MM-dd HH:mm:ss};{Server.Time:yyyy-MM-dd HH:mm:ss};{position.EntryPrice};{position.NetProfit:F2};{resultado}";

                    writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Print("Erro ao salvar no CSV: " + ex.Message);
            }
        }

        private void BuildUI()
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

                _mainPanelBorder = new Border
                {
                    BorderColor = Color.FromArgb(255, 75, 75, 75),
                    BorderThickness = new Thickness(1),
                    Child = mainStack,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(15)
                };

                mainStack.AddChild(CreateText($"ROBÔ: {this.GetType().Name}", Color.Gold, true));
                mainStack.AddChild(CreateDivider());

                _statusText = CreateText("Status: ● ATIVO", Color.Lime, true);
                mainStack.AddChild(_statusText);

                mainStack.AddChild(CreateText($"Ativo: {SymbolName}", Color.WhiteSmoke));

                _spreadText = CreateText("Spread: -- pips", Color.WhiteSmoke);
                mainStack.AddChild(_spreadText);

                mainStack.AddChild(CreateDivider());
                mainStack.AddChild(CreateText($"Meta lucro: +${DailyProfitLimit:F2}", Color.LightSkyBlue));
                mainStack.AddChild(CreateText($"Meta perda: -${DailyLossLimit:F2}", Color.LightPink));

                mainStack.AddChild(CreateDivider());
                _dailyResultText = CreateText("Resultado dia: --", Color.WhiteSmoke, true);
                mainStack.AddChild(_dailyResultText);

                _floatingText = CreateText("Flutuante: --", Color.WhiteSmoke);
                mainStack.AddChild(_floatingText);

                _toggleButton = new Button
                {
                    Text = "DESLIGAR ROBÔ",
                    BackgroundColor = Color.Crimson,
                    ForegroundColor = Color.White,
                    Margin = new Thickness(5, 10, 5, 5),
                    Height = 25,
                    FontWeight = FontWeight.Bold
                };
                _toggleButton.Click += OnToggleButtonClick;

                mainStack.AddChild(CreateDivider());
                mainStack.AddChild(_toggleButton);

                Chart.AddControl(_mainPanelBorder);
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
            _botEnabled = !_botEnabled;

            if (_botEnabled)
            {
                _statusText.Text = "Status: ● ATIVO";
                _statusText.ForegroundColor = Color.Lime;
                _toggleButton.Text = "DESLIGAR ROBÔ";
                _toggleButton.BackgroundColor = Color.Crimson;
            }
            else
            {
                _statusText.Text = "Status: ○ DESATIVADO";
                _statusText.ForegroundColor = Color.Tomato;
                _toggleButton.Text = "LIGAR ROBÔ";
                _toggleButton.BackgroundColor = Color.SeaGreen;
            }
        }

        private void UpdateUI()
        {
            if (_mainPanelBorder == null) return;

            try
            {
                double currentSpreadPips = Symbol.Spread / Symbol.PipSize;
                _spreadText.Text = $"Spread: {currentSpreadPips:F1} / {MaxSpreadPips:F1} pips";

                if (currentSpreadPips > MaxSpreadPips)
                    _spreadText.ForegroundColor = Color.Tomato;
                else
                    _spreadText.ForegroundColor = Color.WhiteSmoke;

                GetDailyAndFloatingProfit(out double dailyClosedProfit, out double floatingProfit);

                _floatingText.Text = $"Flutuante: {(floatingProfit >= 0 ? "+" : "-")}${Math.Abs(floatingProfit):F2}";
                _floatingText.ForegroundColor = floatingProfit >= 0 ? Color.Lime : Color.Tomato;

                if (_stoppedForToday)
                {
                    double totalProfit = dailyClosedProfit + floatingProfit;

                    if (_stoppedByProtection)
                    {
                        _dailyResultText.Text = "LUCRO PROTEGIDO ATINGIDO";
                        _dailyResultText.ForegroundColor = Color.Gold;
                    }
                    else if (DailyProfitLimit > 0 && totalProfit >= DailyProfitLimit)
                    {
                        _dailyResultText.Text = "META DE LUCRO ATINGIDA";
                        _dailyResultText.ForegroundColor = Color.Lime;
                    }
                    else if (DailyLossLimit > 0 && totalProfit <= -DailyLossLimit)
                    {
                        _dailyResultText.Text = "LIMITE DE PERDA ATINGIDO";
                        _dailyResultText.ForegroundColor = Color.Tomato;
                    }
                    else
                    {
                        _dailyResultText.Text = $"Resultado dia: {(dailyClosedProfit >= 0 ? "+" : "-")}${Math.Abs(dailyClosedProfit):F2}";
                        _dailyResultText.ForegroundColor = dailyClosedProfit >= 0 ? Color.Lime : Color.Tomato;
                    }
                }
                else
                {
                    _dailyResultText.Text = $"Resultado dia: {(dailyClosedProfit >= 0 ? "+" : "-")}${Math.Abs(dailyClosedProfit):F2}";
                    _dailyResultText.ForegroundColor = dailyClosedProfit >= 0 ? Color.Lime : Color.Tomato;
                }
            }
            catch (Exception ex)
            {
                Print("Falha ao atualizar painel visual: " + ex.Message);
            }
        }
    }
}