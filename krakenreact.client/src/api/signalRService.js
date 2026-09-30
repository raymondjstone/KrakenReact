import * as signalR from '@microsoft/signalr';

let connection = null;
let connectionPromise = null;

// The server now sends only what changed ("OrdersDelta" / "BalancesDelta") plus a full snapshot to each client as it
// connects. Pages still subscribe to the familiar "OrderUpdate" / "BalanceUpdate" events and expect the complete list,
// so a small store merges deltas into the full list and hands that to those subscribers.
const live = {
  OrderUpdate: { key: 'id', items: new Map(), listeners: new Set() },
  BalanceUpdate: { key: 'asset', items: new Map(), listeners: new Set() },
};

function notify(eventName) {
  const stream = live[eventName];
  const list = Array.from(stream.items.values());
  stream.listeners.forEach(cb => { try { cb(list); } catch (e) { console.error(`${eventName} listener failed`, e); } });
}

function replaceAll(eventName, list) {
  const stream = live[eventName];
  stream.items.clear();
  (list || []).forEach(item => stream.items.set(item[stream.key], item));
  notify(eventName);
}

function applyDelta(eventName, delta) {
  const stream = live[eventName];
  (delta?.changed || []).forEach(item => stream.items.set(item[stream.key], item));
  (delta?.removed || []).forEach(k => stream.items.delete(k));
  notify(eventName);
}

export function getConnection() {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/tradingHub')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // Route subscriptions to the two list events through the store instead of straight to the wire
    const originalOn = connection.on.bind(connection);
    const originalOff = connection.off.bind(connection);
    connection.on = (name, cb) => (live[name] ? live[name].listeners.add(cb) : originalOn(name, cb));
    connection.off = (name, cb) => (live[name] ? live[name].listeners.delete(cb) : originalOff(name, cb));

    // Full snapshots (on connect / resync) replace the list; deltas merge into it
    originalOn('OrderUpdate', list => replaceAll('OrderUpdate', list));
    originalOn('BalanceUpdate', list => replaceAll('BalanceUpdate', list));
    originalOn('OrdersDelta', d => applyDelta('OrderUpdate', d));
    originalOn('BalancesDelta', d => applyDelta('BalanceUpdate', d));

    // Permanent no-op so SignalR never warns "No client method found" for
    // high-frequency events when their page isn't mounted.
    originalOn('AutoTradeUpdate', () => {});
  }
  return connection;
}

export async function startConnection() {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    if (!connectionPromise) {
      connectionPromise = conn.start()
        .then(() => { console.log('SignalR connected'); connectionPromise = null; })
        .catch(err => { console.error('SignalR connection error:', err); connectionPromise = null; setTimeout(() => startConnection(), 5000); });
    }
    await connectionPromise;
  }
  return conn;
}
