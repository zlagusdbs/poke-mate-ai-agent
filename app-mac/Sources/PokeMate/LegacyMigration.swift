import Foundation
import PokeMateCore

enum LegacyMigration {
    static let legacyName = "pocketmate"
    static let legacyStatusItem = "PocketMateStatusItem"

    static func copyDefaults(_ defaults: UserDefaults = .standard) {
        guard !defaults.bool(forKey: "migratedLegacyDefaults"), let current = Bundle.main.bundleIdentifier,
              current.hasSuffix("pokemate") else { return }
        let legacy = String(current.dropLast("pokemate".count)) + legacyName
        if let old = defaults.persistentDomain(forName: legacy) {
            for (key, value) in old {
                let mapped = key.replacingOccurrences(of: legacyStatusItem, with: "PokeMateStatusItem")
                if defaults.object(forKey: mapped) == nil { defaults.set(value, forKey: mapped) }
            }
        }
        defaults.set(true, forKey: "migratedLegacyDefaults")
    }

    static func copyKeychain(settings: AppSettings) {
        guard !UserDefaults.standard.bool(forKey: "migratedLegacyKeychain") else { return }
        Task {
            if (try? await MateCLI.migrateKeychain(settings: settings)) == true {
                await MainActor.run { UserDefaults.standard.set(true, forKey: "migratedLegacyKeychain") }
            }
        }
    }
}
