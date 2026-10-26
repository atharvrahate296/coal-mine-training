import React, { useState, useEffect, useMemo, useCallback } from 'react';
import axios from 'axios';
import {
  ResponsiveContainer,
  ComposedChart,
  Bar,
  Line,
  XAxis,
  YAxis,
  Tooltip,
  ReferenceLine,
  PieChart,
  Pie,
  Cell,
  Legend,
  CartesianGrid,
} from 'recharts';
import {
  HardHat,
  ShieldAlert,
  ShieldCheck,
  RefreshCw,
  Search,
  Filter,
  Layers,
  Smartphone,
  Glasses,
  AlertTriangle,
  Clock,
  Activity,
  CheckCircle2,
  Database,
  ArrowUpRight,
  Sparkles,
} from 'lucide-react';

// API Base URL - Uses Vite proxy (/api) by default, or points directly to localhost:3000
const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:3000';

// Fallback demo data to showcase the industrial UI if backend is empty or unreachable
const DEMO_SESSIONS = [
  {
    _id: 'demo-1',
    traineeId: 'MINER-084',
    mode: 'VR',
    score: 95,
    totalAnswered: 10,
    deviceModel: 'Meta Quest 3',
    timestamp: new Date(Date.now() - 1000 * 60 * 12).toISOString(),
  },
  {
    _id: 'demo-2',
    traineeId: 'MINER-102',
    mode: 'VR',
    score: 80,
    totalAnswered: 10,
    deviceModel: 'Meta Quest Pro',
    timestamp: new Date(Date.now() - 1000 * 60 * 35).toISOString(),
  },
  {
    _id: 'demo-3',
    traineeId: 'SAFETY-019',
    mode: 'AR',
    score: 60,
    totalAnswered: 10,
    deviceModel: 'Samsung Galaxy Tab Active 4 Pro',
    timestamp: new Date(Date.now() - 1000 * 60 * 70).toISOString(),
  },
  {
    _id: 'demo-4',
    traineeId: 'MINER-055',
    mode: 'VR',
    score: 50,
    totalAnswered: 10,
    deviceModel: 'HTC Vive Focus 3',
    timestamp: new Date(Date.now() - 1000 * 60 * 110).toISOString(),
  },
  {
    _id: 'demo-5',
    traineeId: 'ENGINEER-007',
    mode: 'AR',
    score: 90,
    totalAnswered: 10,
    deviceModel: 'RealWear Navigator 520',
    timestamp: new Date(Date.now() - 1000 * 60 * 160).toISOString(),
  },
  {
    _id: 'demo-6',
    traineeId: 'MINER-133',
    mode: 'VR',
    score: 70,
    totalAnswered: 10,
    deviceModel: 'Meta Quest 3',
    timestamp: new Date(Date.now() - 1000 * 60 * 240).toISOString(),
  },
];

export default function App() {
  const [sessions, setSessions] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [backendConnected, setBackendConnected] = useState(false);
  const [isUsingDemo, setIsUsingDemo] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [modeFilter, setModeFilter] = useState('ALL'); // ALL, VR, AR
  const [statusFilter, setStatusFilter] = useState('ALL'); // ALL, PASS, FAIL
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [lastUpdated, setLastUpdated] = useState(new Date());

  // Format nice timestamps for display
  const formatTimestamp = (dateString) => {
    if (!dateString) return 'N/A';
    const date = new Date(dateString);
    if (isNaN(date.getTime())) return dateString;
    return new Intl.DateTimeFormat('en-US', {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: true,
    }).format(date);
  };

  // Fetch training sessions from the Express API
  const fetchSessions = useCallback(async (isSilent = false) => {
    if (!isSilent) setLoading(true);
    try {
      // First try relative path (via Vite proxy) or direct backend URL
      const response = await axios.get(`${API_BASE_URL}/api/dashboard/results`, {
        timeout: 4000,
      });

      if (response.data && Array.isArray(response.data.data)) {
        if (response.data.data.length > 0) {
          setSessions(response.data.data);
          setIsUsingDemo(false);
        } else {
          // If database is currently empty, load demo records so UI isn't blank
          setSessions(DEMO_SESSIONS);
          setIsUsingDemo(true);
        }
        setBackendConnected(true);
        setError(null);
      }
    } catch (err) {
      console.warn('Backend unavailable, falling back to offline demo records:', err.message);
      setBackendConnected(false);
      setError(`Cannot reach backend at ${API_BASE_URL}. Showing demo training telemetry.`);
      // If no data loaded yet, show demo data
      setSessions((prev) => (prev.length > 0 && !isUsingDemo ? prev : DEMO_SESSIONS));
      setIsUsingDemo(true);
    } finally {
      setLoading(false);
      setLastUpdated(new Date());
    }
  }, [isUsingDemo]);

  // Initial load
  useEffect(() => {
    fetchSessions();
  }, [fetchSessions]);

  // Optional auto-refresh polling (useful during live training trials)
  useEffect(() => {
    if (!autoRefresh) return;
    const interval = setInterval(() => {
      fetchSessions(true);
    }, 5000);
    return () => clearInterval(interval);
  }, [autoRefresh, fetchSessions]);

  // Helper calculations for metrics and analytics
  const processedData = useMemo(() => {
    return sessions.map((item) => {
      const total = Number(item.totalAnswered) || 1;
      const scoreNum = Number(item.score) || 0;
      const percentage = Math.round((scoreNum / total) * 100);
      const isPass = percentage >= 70;
      return {
        ...item,
        percentage,
        isPass,
        shortTime: new Date(item.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
      };
    });
  }, [sessions]);

  // Aggregate KPI stats
  const stats = useMemo(() => {
    const total = processedData.length;
    if (total === 0) return { total: 0, passRate: 0, avgPercentage: 0, vrCount: 0, arCount: 0 };

    const passCount = processedData.filter((s) => s.isPass).length;
    const passRate = Math.round((passCount / total) * 100);
    const sumPercentage = processedData.reduce((acc, curr) => acc + curr.percentage, 0);
    const avgPercentage = Math.round(sumPercentage / total);
    const vrCount = processedData.filter((s) => s.mode === 'VR').length;
    const arCount = processedData.filter((s) => s.mode === 'AR').length;

    return { total, passCount, passRate, avgPercentage, vrCount, arCount };
  }, [processedData]);

  // Filtered sessions for the trainee table
  const filteredSessions = useMemo(() => {
    return processedData.filter((item) => {
      const matchSearch =
        item.traineeId.toLowerCase().includes(searchQuery.toLowerCase()) ||
        (item.deviceModel && item.deviceModel.toLowerCase().includes(searchQuery.toLowerCase()));

      const matchMode = modeFilter === 'ALL' || item.mode === modeFilter;
      const matchStatus =
        statusFilter === 'ALL' ||
        (statusFilter === 'PASS' && item.isPass) ||
        (statusFilter === 'FAIL' && !item.isPass);

      return matchSearch && matchMode && matchStatus;
    });
  }, [processedData, searchQuery, modeFilter, statusFilter]);

  // Chart data: Chronological progression (oldest to newest for visual timeline)
  const timelineChartData = useMemo(() => {
    return [...processedData]
      .reverse()
      .map((item, index) => ({
        index: index + 1,
        traineeId: item.traineeId,
        percentage: item.percentage,
        score: item.score,
        total: item.totalAnswered,
        mode: item.mode,
        label: `${item.traineeId} (${item.shortTime})`,
        isPass: item.isPass,
      }));
  }, [processedData]);

  // Mode ratio data for Pie Chart
  const modeChartData = useMemo(() => {
    return [
      { name: 'VR Training', value: stats.vrCount, color: '#38BDF8' }, // Neon Blue
      { name: 'AR Training', value: stats.arCount, color: '#FFD966' }, // Warning Yellow
    ].filter((item) => item.value > 0);
  }, [stats]);

  return (
    <div className="min-h-screen bg-[#090b10] text-slate-100 flex flex-col font-sans selection:bg-[#FFD966] selection:text-black">
      {/* Top Industrial Hazard Accent Line */}
      <div className="h-2 w-full hazard-stripes border-b border-[#232b3d] shadow-[0_2px_10px_rgba(255,217,102,0.15)]" />

      {/* Main Container */}
      <div className="max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-6 space-y-6 flex-1">
        
        {/* ========================================================= */}
        {/* SECTION 1: HEADER */}
        {/* ========================================================= */}
        <header className="bg-[#121622] border border-[#232b3d] rounded-xl p-5 shadow-2xl relative overflow-hidden">
          {/* Subtle industrial background glow */}
          <div className="absolute top-0 right-0 -mt-10 -mr-10 w-64 h-64 bg-[#FFD966]/5 rounded-full blur-3xl pointer-events-none" />

          <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
            {/* Title & Brand */}
            <div className="flex items-center space-x-4">
              <div className="p-3 bg-[#1a2030] border border-[#FFD966]/40 rounded-lg shadow-inner text-[#FFD966] flex items-center justify-center">
                <HardHat className="w-8 h-8 stroke-[2.2]" />
              </div>
              <div>
                <div className="flex items-center space-x-3">
                  <h1 className="text-2xl sm:text-3xl font-extrabold tracking-wide uppercase font-mono text-slate-50 flex items-center gap-2">
                    <span>CoalMineVR</span>
                    <span className="text-slate-500 font-light">/</span>
                    <span className="text-[#FFD966] glow-yellow">Trainer Dashboard</span>
                  </h1>
                </div>
                <p className="text-xs sm:text-sm text-slate-400 font-mono tracking-wider mt-0.5 flex items-center gap-2">
                  <span className="inline-block w-2 h-2 rounded-full bg-[#4ADE80] animate-pulse" />
                  HAZARD AVOIDANCE & MINE RESCUE SAFETY TELEMETRY SYSTEM
                </p>
              </div>
            </div>

            {/* Server Connectivity, Time & Refresh Controls */}
            <div className="flex flex-wrap items-center gap-3">
              {/* Backend Status Badge */}
              <div
                className={`flex items-center gap-2 px-3 py-1.5 rounded-md text-xs font-mono border ${
                  backendConnected
                    ? 'bg-[#4ADE80]/10 border-[#4ADE80]/40 text-[#4ADE80]'
                    : 'bg-[#FFD966]/10 border-[#FFD966]/40 text-[#FFD966]'
                }`}
              >
                <Database className="w-3.5 h-3.5" />
                <span>{backendConnected ? 'API CONNECTED (PORT 3000)' : 'OFFLINE / DEMO TELEMETRY'}</span>
              </div>

              {/* Auto Refresh Toggle */}
              <button
                type="button"
                onClick={() => setAutoRefresh(!autoRefresh)}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-mono font-medium border transition-colors ${
                  autoRefresh
                    ? 'bg-[#38BDF8]/20 border-[#38BDF8] text-[#38BDF8]'
                    : 'bg-[#181d2a] border-[#2b3548] text-slate-400 hover:text-slate-200'
                }`}
                title="Toggle automatic polling every 5s"
              >
                <Activity className={`w-3.5 h-3.5 ${autoRefresh ? 'animate-spin' : ''}`} />
                <span>LIVE SYNC: {autoRefresh ? 'ON (5s)' : 'OFF'}</span>
              </button>

              {/* Manual Refresh Button */}
              <button
                type="button"
                onClick={() => fetchSessions()}
                disabled={loading}
                className="flex items-center gap-2 px-3.5 py-1.5 rounded-md text-xs font-mono font-semibold bg-[#FFD966] hover:bg-[#ffe38a] text-black transition-all shadow-md active:scale-95 disabled:opacity-50"
              >
                <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} />
                <span>REFRESH</span>
              </button>
            </div>
          </div>

          {/* Offline/Warning Alert Banner if backend is disconnected */}
          {error && (
            <div className="mt-4 p-3 bg-amber-950/40 border border-amber-600/40 rounded-lg flex items-center justify-between text-xs text-amber-300 font-mono">
              <div className="flex items-center gap-2">
                <AlertTriangle className="w-4 h-4 text-[#FFD966] shrink-0" />
                <span>{error}</span>
              </div>
              <span className="text-slate-400 text-[11px] hidden sm:inline">Start server with: `node server.js`</span>
            </div>
          )}
        </header>

        {/* ========================================================= */}
        {/* KPI OVERVIEW CARDS */}
        {/* ========================================================= */}
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          {/* Card 1: Total Sessions */}
          <div className="bg-[#121622] border border-[#232b3d] p-4 rounded-xl shadow-lg relative overflow-hidden group hover:border-[#384661] transition-all">
            <div className="flex items-center justify-between text-slate-400 mb-1">
              <span className="text-xs uppercase font-mono tracking-wider">Total Sessions</span>
              <Layers className="w-4 h-4 text-slate-400 group-hover:text-[#FFD966] transition-colors" />
            </div>
            <div className="text-2xl sm:text-3xl font-extrabold font-mono text-slate-100">{stats.total}</div>
            <div className="text-[11px] text-slate-400 font-mono mt-1">Logged mine evaluations</div>
          </div>

          {/* Card 2: Average Score */}
          <div className="bg-[#121622] border border-[#232b3d] p-4 rounded-xl shadow-lg relative overflow-hidden group hover:border-[#384661] transition-all">
            <div className="flex items-center justify-between text-slate-400 mb-1">
              <span className="text-xs uppercase font-mono tracking-wider">Average Score</span>
              <Activity className="w-4 h-4 text-[#FFD966]" />
            </div>
            <div className="text-2xl sm:text-3xl font-extrabold font-mono text-[#FFD966]">
              {stats.avgPercentage}%
            </div>
            <div className="text-[11px] text-slate-400 font-mono mt-1">Benchmark: 70% required</div>
          </div>

          {/* Card 3: Safety Compliance / Pass Rate */}
          <div className="bg-[#121622] border border-[#232b3d] p-4 rounded-xl shadow-lg relative overflow-hidden group hover:border-[#384661] transition-all">
            <div className="flex items-center justify-between text-slate-400 mb-1">
              <span className="text-xs uppercase font-mono tracking-wider">Pass Rate</span>
              <ShieldCheck className="w-4 h-4 text-[#4ADE80]" />
            </div>
            <div className="text-2xl sm:text-3xl font-extrabold font-mono text-[#4ADE80] glow-green">
              {stats.passRate}%
            </div>
            <div className="text-[11px] text-slate-400 font-mono mt-1">
              {stats.passCount} of {stats.total} trainees qualified
            </div>
          </div>

          {/* Card 4: Hardware / Mode Split */}
          <div className="bg-[#121622] border border-[#232b3d] p-4 rounded-xl shadow-lg relative overflow-hidden group hover:border-[#384661] transition-all">
            <div className="flex items-center justify-between text-slate-400 mb-1">
              <span className="text-xs uppercase font-mono tracking-wider">Hardware Split</span>
              <Glasses className="w-4 h-4 text-cyan-400" />
            </div>
            <div className="text-xl sm:text-2xl font-extrabold font-mono text-slate-100 flex items-center gap-3">
              <span className="text-cyan-400">VR: {stats.vrCount}</span>
              <span className="text-slate-600">|</span>
              <span className="text-[#FFD966]">AR: {stats.arCount}</span>
            </div>
            <div className="text-[11px] text-slate-400 font-mono mt-1">Headset vs Tablet field tests</div>
          </div>
        </div>

        {/* ========================================================= */}
        {/* SECTION 2: ANALYTICS OVERVIEW (TOP) */}
        {/* ========================================================= */}
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Main Bar/Area Chart: Scores Over Time */}
          <div className="lg:col-span-2 bg-[#121622] border border-[#232b3d] rounded-xl p-5 shadow-2xl flex flex-col justify-between">
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mb-4 pb-3 border-b border-[#232b3d]">
              <div>
                <h2 className="text-lg font-bold font-mono text-slate-100 flex items-center gap-2">
                  <Activity className="w-4 h-4 text-[#FFD966]" />
                  <span>TRAINEE SCORE PERFORMANCE TIMELINE</span>
                </h2>
                <p className="text-xs text-slate-400 font-mono">
                  Visual score percentage per training session with safety pass line at 70%
                </p>
              </div>

              {/* Legend Badges */}
              <div className="flex items-center gap-3 text-xs font-mono">
                <span className="flex items-center gap-1.5 text-slate-300">
                  <span className="w-2.5 h-2.5 rounded-sm bg-[#4ADE80]" />
                  Pass (≥70%)
                </span>
                <span className="flex items-center gap-1.5 text-slate-300">
                  <span className="w-2.5 h-2.5 rounded-sm bg-[#F87171]" />
                  Failed (&lt;70%)
                </span>
              </div>
            </div>

            {/* Recharts Performance Chart */}
            <div className="h-64 sm:h-72 w-full">
              <ResponsiveContainer width="100%" height="100%">
                <ComposedChart data={timelineChartData} margin={{ top: 10, right: 10, left: -20, bottom: 20 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="#1f2736" vertical={false} />
                  <XAxis
                    dataKey="label"
                    stroke="#64748b"
                    tick={{ fill: '#94a3b8', fontSize: 11, fontFamily: 'monospace' }}
                    angle={-20}
                    textAnchor="end"
                    interval={0}
                  />
                  <YAxis
                    domain={[0, 100]}
                    stroke="#64748b"
                    tick={{ fill: '#94a3b8', fontSize: 11, fontFamily: 'monospace' }}
                    tickFormatter={(val) => `${val}%`}
                  />
                  <Tooltip
                    content={({ active, payload }) => {
                      if (!active || !payload || !payload.length) return null;
                      const data = payload[0].payload;
                      return (
                        <div className="bg-[#0b0e14] border border-[#3b475e] p-3 rounded-md shadow-xl font-mono text-xs space-y-1">
                          <div className="font-bold text-[#FFD966] flex items-center justify-between gap-4">
                            <span>{data.traineeId}</span>
                            <span className="px-1.5 py-0.5 rounded text-[10px] bg-slate-800 text-slate-300">
                              {data.mode}
                            </span>
                          </div>
                          <div className="text-slate-300">
                            Score: <span className="font-bold text-white">{data.score} / {data.total}</span> ({data.percentage}%)
                          </div>
                          <div className={data.isPass ? 'text-[#4ADE80] font-semibold' : 'text-[#F87171] font-semibold'}>
                            Status: {data.isPass ? '✓ PASSED SAFETY' : '⚠ NEEDS RETRAINING'}
                          </div>
                        </div>
                      );
                    }}
                  />
                  {/* Passing Threshold 70% Reference Line */}
                  <ReferenceLine
                    y={70}
                    stroke="#FFD966"
                    strokeDasharray="4 4"
                    strokeWidth={1.5}
                    label={{
                      value: 'PASS REQUIREMENT (70%)',
                      position: 'insideTopRight',
                      fill: '#FFD966',
                      fontSize: 10,
                      fontFamily: 'monospace',
                    }}
                  />
                  <Bar dataKey="percentage" radius={[4, 4, 0, 0]} maxBarSize={45}>
                    {timelineChartData.map((entry, index) => (
                      <Cell
                        key={`cell-${index}`}
                        fill={entry.percentage >= 70 ? '#4ADE80' : '#F87171'}
                        fillOpacity={0.85}
                      />
                    ))}
                  </Bar>
                  <Line
                    type="monotone"
                    dataKey="percentage"
                    stroke="#FFD966"
                    strokeWidth={2}
                    dot={{ fill: '#FFD966', r: 3 }}
                  />
                </ComposedChart>
              </ResponsiveContainer>
            </div>
          </div>

          {/* Secondary Chart: VR vs AR Training Ratio Pie/Donut */}
          <div className="bg-[#121622] border border-[#232b3d] rounded-xl p-5 shadow-2xl flex flex-col justify-between">
            <div className="mb-2 pb-3 border-b border-[#232b3d]">
              <h2 className="text-lg font-bold font-mono text-slate-100 flex items-center gap-2">
                <Glasses className="w-4 h-4 text-cyan-400" />
                <span>AR VS VR DEPLOYMENT RATIO</span>
              </h2>
              <p className="text-xs text-slate-400 font-mono">
                Distribution of immersive headset vs mobile AR sessions
              </p>
            </div>

            <div className="h-56 sm:h-64 w-full relative flex items-center justify-center">
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={modeChartData}
                    cx="50%"
                    cy="50%"
                    innerRadius={55}
                    outerRadius={80}
                    paddingAngle={6}
                    dataKey="value"
                  >
                    {modeChartData.map((entry, index) => (
                      <Cell key={`slice-${index}`} fill={entry.color} stroke="#121622" strokeWidth={2} />
                    ))}
                  </Pie>
                  <Tooltip
                    content={({ active, payload }) => {
                      if (!active || !payload || !payload.length) return null;
                      const d = payload[0];
                      return (
                        <div className="bg-[#0b0e14] border border-[#3b475e] p-2 rounded shadow font-mono text-xs">
                          <span style={{ color: d.payload.color }}>{d.name}: </span>
                          <span className="font-bold text-white">{d.value} sessions</span>
                        </div>
                      );
                    }}
                  />
                  <Legend
                    verticalAlign="bottom"
                    iconType="circle"
                    formatter={(val) => <span className="text-xs font-mono text-slate-300">{val}</span>}
                  />
                </PieChart>
              </ResponsiveContainer>

              {/* Total count badge inside donut */}
              <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none pb-6">
                <span className="text-2xl font-black font-mono text-slate-100">{stats.total}</span>
                <span className="text-[10px] font-mono text-slate-400 uppercase">Total</span>
              </div>
            </div>

            {/* Quick breakdown footer */}
            <div className="grid grid-cols-2 gap-2 pt-2 border-t border-[#232b3d] text-center font-mono text-xs">
              <div className="bg-[#181d2a] p-2 rounded border border-[#2b3548]">
                <div className="text-cyan-400 font-bold">VR IMMERSIVE</div>
                <div className="text-slate-300">{stats.vrCount} ({stats.total > 0 ? Math.round((stats.vrCount / stats.total) * 100) : 0}%)</div>
              </div>
              <div className="bg-[#181d2a] p-2 rounded border border-[#2b3548]">
                <div className="text-[#FFD966] font-bold">AR FIELD TEST</div>
                <div className="text-slate-300">{stats.arCount} ({stats.total > 0 ? Math.round((stats.arCount / stats.total) * 100) : 0}%)</div>
              </div>
            </div>
          </div>
        </div>

        {/* ========================================================= */}
        {/* SECTION 3: TRAINEE RESULTS TABLE (BOTTOM) */}
        {/* ========================================================= */}
        <div className="bg-[#121622] border border-[#232b3d] rounded-xl shadow-2xl overflow-hidden">
          {/* Table Controls Header */}
          <div className="p-4 sm:p-5 border-b border-[#232b3d] flex flex-col md:flex-row md:items-center justify-between gap-4">
            <div>
              <h2 className="text-lg font-bold font-mono text-slate-100 flex items-center gap-2">
                <ShieldAlert className="w-5 h-5 text-[#FFD966]" />
                <span>TRAINEE SAFETY ASSESSMENT LOG</span>
              </h2>
              <p className="text-xs text-slate-400 font-mono mt-0.5">
                Detailed record of trainee responses, devices, and safety pass/fail compliance.
              </p>
            </div>

            {/* Filter and Search Bar */}
            <div className="flex flex-wrap items-center gap-3">
              {/* Search Box */}
              <div className="relative">
                <Search className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
                <input
                  type="text"
                  placeholder="Search Trainee or Device..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="bg-[#181d2a] border border-[#2b3548] rounded-md pl-9 pr-3 py-1.5 text-xs font-mono text-slate-200 placeholder-slate-500 focus:outline-none focus:border-[#FFD966] w-48 sm:w-60 transition-colors"
                />
              </div>

              {/* Mode Filter Dropdown */}
              <div className="flex items-center space-x-1 bg-[#181d2a] border border-[#2b3548] rounded-md p-0.5 text-xs font-mono">
                {['ALL', 'VR', 'AR'].map((mode) => (
                  <button
                    key={mode}
                    onClick={() => setModeFilter(mode)}
                    className={`px-2.5 py-1 rounded text-xs transition-colors ${
                      modeFilter === mode
                        ? 'bg-[#FFD966] text-black font-bold'
                        : 'text-slate-400 hover:text-slate-200'
                    }`}
                  >
                    {mode}
                  </button>
                ))}
              </div>

              {/* Status Filter */}
              <div className="flex items-center space-x-1 bg-[#181d2a] border border-[#2b3548] rounded-md p-0.5 text-xs font-mono">
                {[
                  { key: 'ALL', label: 'All Status' },
                  { key: 'PASS', label: 'Passed ≥70%' },
                  { key: 'FAIL', label: 'Warning <70%' },
                ].map((s) => (
                  <button
                    key={s.key}
                    onClick={() => setStatusFilter(s.key)}
                    className={`px-2.5 py-1 rounded text-xs transition-colors ${
                      statusFilter === s.key
                        ? s.key === 'FAIL'
                          ? 'bg-[#F87171] text-black font-bold'
                          : 'bg-[#4ADE80] text-black font-bold'
                        : 'text-slate-400 hover:text-slate-200'
                    }`}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
            </div>
          </div>

          {/* Results Table */}
          <div className="overflow-x-auto">
            <table className="w-full text-left border-collapse font-mono text-xs">
              <thead>
                <tr className="bg-[#0e111a] text-slate-400 uppercase tracking-wider border-b border-[#232b3d]">
                  <th className="py-3 px-4 font-semibold">Trainee ID</th>
                  <th className="py-3 px-4 font-semibold">Mode</th>
                  <th className="py-3 px-4 font-semibold">Raw Score</th>
                  <th className="py-3 px-4 font-semibold">Percentage</th>
                  <th className="py-3 px-4 font-semibold">Status</th>
                  <th className="py-3 px-4 font-semibold">Device Model</th>
                  <th className="py-3 px-4 font-semibold">Timestamp</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[#1e2535]">
                {filteredSessions.length > 0 ? (
                  filteredSessions.map((session) => {
                    const isBelowThreshold = session.percentage < 70;

                    return (
                      <tr
                        key={session._id}
                        className={`transition-colors ${
                          isBelowThreshold
                            ? 'bg-red-950/25 hover:bg-red-900/40 border-l-4 border-l-[#F87171]'
                            : 'hover:bg-[#181d2a]/70 border-l-4 border-l-transparent'
                        }`}
                      >
                        {/* Trainee ID */}
                        <td className="py-3.5 px-4 font-bold text-slate-100 flex items-center gap-2">
                          <span className="w-7 h-7 rounded bg-[#1e2538] flex items-center justify-center text-slate-300 font-mono text-xs border border-[#2d3852]">
                            {session.traineeId.slice(0, 2).toUpperCase()}
                          </span>
                          <span className={isBelowThreshold ? 'text-red-200' : 'text-slate-100'}>
                            {session.traineeId}
                          </span>
                        </td>

                        {/* Mode Badge */}
                        <td className="py-3.5 px-4">
                          <span
                            className={`inline-flex items-center gap-1 px-2.5 py-0.5 rounded text-[11px] font-bold border ${
                              session.mode === 'VR'
                                ? 'bg-cyan-950/60 text-cyan-300 border-cyan-700/50'
                                : 'bg-amber-950/60 text-[#FFD966] border-amber-600/50'
                            }`}
                          >
                            {session.mode === 'VR' ? (
                              <Glasses className="w-3 h-3" />
                            ) : (
                              <Smartphone className="w-3 h-3" />
                            )}
                            {session.mode}
                          </span>
                        </td>

                        {/* Raw Score */}
                        <td className="py-3.5 px-4 text-slate-300">
                          <span className="font-semibold text-white">{session.score}</span>
                          <span className="text-slate-500 font-normal"> / {session.totalAnswered}</span>
                        </td>

                        {/* Percentage with progress bar */}
                        <td className="py-3.5 px-4">
                          <div className="flex items-center gap-2">
                            <span
                              className={`font-bold text-sm ${
                                isBelowThreshold ? 'text-[#F87171]' : 'text-[#4ADE80]'
                              }`}
                            >
                              {session.percentage}%
                            </span>
                            <div className="w-16 h-1.5 bg-[#181e2b] rounded-full overflow-hidden border border-[#263147]">
                              <div
                                className={`h-full rounded-full ${
                                  isBelowThreshold ? 'bg-[#F87171]' : 'bg-[#4ADE80]'
                                }`}
                                style={{ width: `${Math.min(100, Math.max(0, session.percentage))}%` }}
                              />
                            </div>
                          </div>
                        </td>

                        {/* Status (Passed / Warning) */}
                        <td className="py-3.5 px-4">
                          {isBelowThreshold ? (
                            <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] font-bold bg-red-900/50 text-[#F87171] border border-red-700/60">
                              <AlertTriangle className="w-3 h-3" />
                              NEEDS RETRAINING
                            </span>
                          ) : (
                            <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] font-bold bg-emerald-950/60 text-[#4ADE80] border border-emerald-700/50">
                              <CheckCircle2 className="w-3 h-3" />
                              SAFETY QUALIFIED
                            </span>
                          )}
                        </td>

                        {/* Device Model */}
                        <td className="py-3.5 px-4 text-slate-300">
                          <span className="inline-block max-w-[180px] truncate" title={session.deviceModel}>
                            {session.deviceModel || 'Unknown Hardware'}
                          </span>
                        </td>

                        {/* Formatted Timestamp */}
                        <td className="py-3.5 px-4 text-slate-400 text-[11px] whitespace-nowrap">
                          <div className="flex items-center gap-1.5">
                            <Clock className="w-3 h-3 text-slate-500" />
                            <span>{formatTimestamp(session.timestamp)}</span>
                          </div>
                        </td>
                      </tr>
                    );
                  })
                ) : (
                  <tr>
                    <td colSpan={7} className="py-12 text-center text-slate-400">
                      <div className="flex flex-col items-center justify-center space-y-2">
                        <AlertTriangle className="w-8 h-8 text-[#FFD966]" />
                        <p className="text-sm font-semibold text-slate-200">No training sessions matched filters</p>
                        <p className="text-xs text-slate-500">
                          Try adjusting the search query or reset the mode/status filters.
                        </p>
                      </div>
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          {/* Table Footer */}
          <div className="p-3 bg-[#0e111a] border-t border-[#232b3d] flex flex-col sm:flex-row sm:items-center justify-between text-xs font-mono text-slate-400 gap-2">
            <div>
              Showing <span className="font-bold text-slate-200">{filteredSessions.length}</span> of{' '}
              <span className="font-bold text-slate-200">{sessions.length}</span> recorded sessions
            </div>
            <div className="flex items-center gap-4 text-[11px]">
              <span className="flex items-center gap-1">
                <span className="w-2 h-2 rounded-full bg-[#4ADE80]" />
                Passing threshold: ≥70%
              </span>
              <span className="flex items-center gap-1">
                <span className="w-2 h-2 rounded-full bg-[#F87171]" />
                Red highlight: &lt;70%
              </span>
              <span>Updated: {lastUpdated.toLocaleTimeString()}</span>
            </div>
          </div>
        </div>

      </div>

      {/* Industrial Footer */}
      <footer className="mt-8 border-t border-[#1e2638] bg-[#0c0f16] py-4 text-center text-xs font-mono text-slate-500">
        <div className="max-w-7xl mx-auto px-4 flex flex-col sm:flex-row items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-[#FFD966]" />
            <span>CoalMineVR Telemetry Engine • Android VR/AR Client Sync</span>
          </div>
          <div>Offline-First Field Training Infrastructure</div>
        </div>
      </footer>
    </div>
  );
}
