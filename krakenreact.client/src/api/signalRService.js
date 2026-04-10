import * as signalR from '@microsoft/signalr';

let connection = null;
let connectionPromise = null;

export function getConnection() {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/tradingHub')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();
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
