import * as signalR from '@microsoft/signalr';

let connection = null;

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
    try {
      await conn.start();
      console.log('SignalR connected');
    } catch (err) {
      console.error('SignalR connection error:', err);
      setTimeout(() => startConnection(), 5000);
    }
  }
  return conn;
}
