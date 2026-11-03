import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import {
  api,
  ApiError,
  clearTokens,
  getAccessToken,
  getRefreshToken,
  storeTokens,
} from "../api/client";
import type { CurrentUser, TokenPair } from "../api/types";

interface AuthState {
  user: CurrentUser | null;
  loading: boolean;
  /** Resolves with the freshly signed-in user, so a caller can route by capability immediately
   * rather than reading stale state from a closure. */
  login(email: string, password: string): Promise<CurrentUser>;
  logout(): Promise<void>;
}
const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [loading, setLoading] = useState(true);
  const restore = async () => {
    try {
      if (getAccessToken())
        setUser((await api.get<CurrentUser>("/users/me")).data);
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) clearTokens();
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void restore();
    const expired = () => {
      clearTokens();
      setUser(null);
    };
    window.addEventListener("aerocompliance:expired", expired);
    return () => window.removeEventListener("aerocompliance:expired", expired);
  }, []);
  const login = async (email: string, password: string) => {
    const tokens = (
      await api.post<TokenPair>("/auth/login", { email, password })
    ).data;
    storeTokens(tokens);
    try {
      const me = (await api.get<CurrentUser>("/users/me")).data;
      setUser(me);
      return me;
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) clearTokens();
      throw e;
    }
  };
  const logout = async () => {
    const refreshToken = getRefreshToken();
    try {
      if (refreshToken) await api.post("/auth/logout", { refreshToken });
    } finally {
      clearTokens();
      setUser(null);
    }
  };
  return (
    <AuthContext.Provider value={{ user, loading, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}
export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("AuthProvider missing");
  return value;
}
// Authorization decisions live in ../auth/capabilities.ts, not here — that module is the single
// source of truth mirroring the backend's actual policies. A raw role-list helper like the one
// that used to live here is exactly the scattered-check pattern that made the frontend drift out
// of sync with the backend; it was removed once its last caller was migrated.
