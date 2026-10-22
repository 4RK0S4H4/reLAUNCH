/**
 * Wire protocol between the PC (ReLaunch.Service) and the relay. JSON text frames.
 *
 *  relay → PC   { id, type: "request", method, params }      methods: start | stop | restart | setFavorites | getIcon
 *  PC → relay   { id, type: "response", ok, result?, error?, code? }
 *  PC → relay   { id, type: "request", method, params }      methods: registerPairingCode | revokeDevice | listDevices
 *  relay → PC   { id, type: "response", ... }
 *  PC → relay   { type: "event", name, data }                names: hello | heartbeat | catalogue | status | action | session
 *  relay → PC   { type: "event", name: "devicePaired", data: { deviceId, name } }
 */

export interface AppStatus {
  running: boolean;
  processCount: number;
  uptimeSeconds: number;
  workingSetBytes: number;
}

export interface AppInfo {
  id: string;
  name: string;
  favorite: boolean;
  trackable: boolean;
  iconHash: string | null;
  status: AppStatus;
  phase: string;
}

export interface Catalogue {
  apps: AppInfo[];
  /** Favorite app ids in display order. */
  favorites: string[];
}

export type SessionState = "active" | "locked" | "noSession" | "starting" | "unknown";

export interface Frame {
  id?: string;
  type: "request" | "response" | "event";
  method?: string;
  params?: any;
  ok?: boolean;
  result?: any;
  error?: string;
  code?: string;
  name?: string;
  data?: any;
}
